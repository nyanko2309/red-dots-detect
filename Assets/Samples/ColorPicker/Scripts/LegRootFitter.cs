using UnityEngine;
using UnityEngine.Animations.Rigging;
using Debug = UnityEngine.Debug;

public class LegRootFitter : MonoBehaviour
{
    [Header("Hardware & Decision")]
    public BodyTracker bodyTracker;
    public LegSideSelector sideSelector;
    public Transform hmdTransform;

    [Header("Body Estimator")]
    public SittingBodyEstimator bodyEstimator;

    [Header("Avatar Structure")]
    public Transform avatarRoot;
    public Transform pelvisBone;
    public Transform headBone;

    [Header("IK Constraints")]
    public TwoBoneIKConstraint leftIK;
    public TwoBoneIKConstraint rightIK;

    [Header("IK Target Transforms")]
    public Transform leftAnkleTarget;
    public Transform leftKneeHint;
    public Transform rightAnkleTarget;
    public Transform rightKneeHint;

    [Header("Settings")]
    public float hipHeightBelowHMD = 0.55f;
    public float verticalOffset = -0.05f;
    [Tooltip("Minimum gap between legs")]
    public float baseStanceWidth = 0.15f;
    [Tooltip("Scale multiplier applied on top of computed scale — increase to make avatar bigger")]
    [Range(1f, 2f)]
    public float sizeMultiplier = 1.15f;
    public bool hideUpperBody = true;
    public int printEveryNFrames = 30;

    [Header("Smoothing (used when bodyEstimator is null)")]
    public float smoothSpeedFast = 15f;
    public float smoothSpeedSlow = 4f;
    public float slowVelocityThreshold = 0.05f;

    private bool _hasResolvedBones = false;
    private string _sideLabel = "None";
    private TwoBoneIKConstraint _activeIK;
    private TwoBoneIKConstraint _mirrorIK;

    // Hip is locked in world space at the moment of selection — not driven by HMD
    private Vector3 _lockedHipWorld;
    private Quaternion _lockedBodyRot = Quaternion.identity;
    private bool _hipLocked = false;

    private float _unscaledAvatarThigh = 0f;

    // Fallback smoothing when no estimator
    private Vector3 _smoothedKnee;
    private Vector3 _smoothedAnkle;
    private Vector3 _prevAnkle;
    private bool _smoothingInitialized = false;

    void Start()
    {
        avatarRoot.localScale = Vector3.zero;
        if (hideUpperBody) HideUpperBodyParts();
    }

    void LateUpdate()
    {
        if (bodyTracker == null || hmdTransform == null || sideSelector == null) return;

        if (!sideSelector.sideLocked)
        {
            _hasResolvedBones = false;
            _smoothingInitialized = false;
            _hipLocked = false;
            avatarRoot.localScale = Vector3.zero;
            DisableBothIKs();
            return;
        }

        if (!_hasResolvedBones)
        {
            avatarRoot.localScale = Vector3.one;
            ResolveIKWeightsOnce();
        }

        if (!bodyTracker.KneeValid || !bodyTracker.AnkleValid) return;

        // Get smoothed positions
        Vector3 kneePos, anklePos;
        if (bodyEstimator != null)
        {
            kneePos = bodyEstimator.SmoothedKnee;
            anklePos = bodyEstimator.SmoothedAnkle;
        }
        else
        {
            if (!_smoothingInitialized)
            {
                _smoothedKnee = bodyTracker.KneePosition;
                _smoothedAnkle = bodyTracker.AnklePosition;
                _prevAnkle = _smoothedAnkle;
                _smoothingInitialized = true;
            }
            float vel = Vector3.Distance(bodyTracker.AnklePosition, _prevAnkle) / Time.deltaTime;
            float speed = vel < slowVelocityThreshold ? smoothSpeedSlow : smoothSpeedFast;
            _smoothedKnee = Vector3.Lerp(_smoothedKnee, bodyTracker.KneePosition, speed * Time.deltaTime);
            _smoothedAnkle = Vector3.Lerp(_smoothedAnkle, bodyTracker.AnklePosition, speed * Time.deltaTime);
            _prevAnkle = _smoothedAnkle;

            // Drive active leg ourselves
            UpdateActiveIKTargets(_smoothedKnee, _smoothedAnkle);

            kneePos = _smoothedKnee;
            anklePos = _smoothedAnkle;
        }

        // 1. Pin hip — use AI estimator hip if available, else locked world position
        PinHip();

        // 2. Scale
        ApplyDynamicScaling(kneePos);

        // 3. Mirror leg
        UpdateMirrorIKTargets(kneePos, anklePos);

        // 4. Enforce separation
        EnforceMinLegSeparation();

        // 5. Pin head
        if (headBone)
        {
            headBone.position = hmdTransform.position;
            headBone.rotation = hmdTransform.rotation;
        }

        if (Time.frameCount % printEveryNFrames == 0)
            PrintBodyDebug(kneePos, anklePos);
    }

    void ResolveIKWeightsOnce()
    {
        bool isLeft = sideSelector.currentSide == LegSideSelector.LegSide.Left;
        _sideLabel = isLeft ? "Left" : "Right";

        _activeIK = isLeft ? leftIK : rightIK;
        _mirrorIK = isLeft ? rightIK : leftIK;

        if (leftIK) leftIK.weight = 1f;
        if (rightIK) rightIK.weight = 1f;

        if (_activeIK != null)
            _unscaledAvatarThigh = Vector3.Distance(
                _activeIK.data.root.position,
                _activeIK.data.mid.position);

        // Lock hip position AND rotation at selection moment
        if (bodyEstimator != null && bodyEstimator.HasAIHip)
            _lockedHipWorld = bodyEstimator.AIHipPosition;
        else
            _lockedHipWorld = hmdTransform.position
                            + Vector3.down * hipHeightBelowHMD
                            + Vector3.up * verticalOffset;

        // Lock the avatar forward direction from HMD at this moment — never update again
        Vector3 lockedForward = hmdTransform.forward;
        lockedForward.y = 0f;
        if (lockedForward.sqrMagnitude < 0.001f) lockedForward = Vector3.forward;
        _lockedBodyRot = Quaternion.LookRotation(lockedForward.normalized, Vector3.up);
        _hipLocked = true;

        _hasResolvedBones = true;
        Debug.Log($"[body] IK: {_sideLabel} | Thigh: {_unscaledAvatarThigh:F3}m | Hip: {_lockedHipWorld:F2}");
    }

    void DisableBothIKs()
    {
        if (leftIK) leftIK.weight = 0f;
        if (rightIK) rightIK.weight = 0f;
        _sideLabel = "None";
    }

    void PinHip()
    {
        Vector3 hipPos = _hipLocked ? _lockedHipWorld
                       : hmdTransform.position + Vector3.down * hipHeightBelowHMD + Vector3.up * verticalOffset;

        // Only update Y from AI estimator — XZ stays locked
        if (bodyEstimator != null && bodyEstimator.HasAIHip)
            hipPos = new Vector3(_lockedHipWorld.x, bodyEstimator.AIHipPosition.y, _lockedHipWorld.z);

        // Always use the rotation locked at selection time — never follow HMD rotation
        Quaternion rot = _hipLocked ? _lockedBodyRot : Quaternion.identity;

        Vector3 pelvisOffset = avatarRoot.InverseTransformPoint(pelvisBone.position);
        avatarRoot.position = hipPos - (rot * pelvisOffset);
        avatarRoot.rotation = rot;
    }

    void ApplyDynamicScaling(Vector3 kneePos)
    {
        if (_unscaledAvatarThigh < 0.05f) return;

        Vector3 hipPos = _hipLocked ? _lockedHipWorld :
                         hmdTransform.position + Vector3.down * hipHeightBelowHMD;

        float target = Vector3.Distance(hipPos, kneePos);
        if (target < 0.05f) return;

        // Apply size multiplier to make avatar slightly bigger than detected
        avatarRoot.localScale = Vector3.one * (target / _unscaledAvatarThigh) * sizeMultiplier;
    }

    void UpdateActiveIKTargets(Vector3 kneePos, Vector3 anklePos)
    {
        Transform ankleT = (_activeIK == leftIK) ? leftAnkleTarget : rightAnkleTarget;
        Transform kneeT = (_activeIK == leftIK) ? leftKneeHint : rightKneeHint;

        if (ankleT) ankleT.position = anklePos;
        if (kneeT) kneeT.position = kneePos;
    }

    void UpdateMirrorIKTargets(Vector3 kneePos, Vector3 anklePos)
    {
        if (_mirrorIK == null) return;

        Transform ankleT = (_mirrorIK == leftIK) ? leftAnkleTarget : rightAnkleTarget;
        Transform kneeT = (_mirrorIK == leftIK) ? leftKneeHint : rightKneeHint;

        Vector3 localAnkle = avatarRoot.InverseTransformPoint(anklePos);
        Vector3 localKnee = avatarRoot.InverseTransformPoint(kneePos);

        localAnkle.x = -localAnkle.x;
        localKnee.x = -localKnee.x;

        bool mirrorIsLeft = (_mirrorIK == leftIK);
        float sideShift = baseStanceWidth * (mirrorIsLeft ? -1f : 1f);
        localAnkle.x += sideShift;
        localKnee.x += sideShift;

        // Hard clamp — mirror leg never crosses centre
        if (mirrorIsLeft)
        {
            localAnkle.x = Mathf.Min(localAnkle.x, -0.01f);
            localKnee.x = Mathf.Min(localKnee.x, -0.01f);
        }
        else
        {
            localAnkle.x = Mathf.Max(localAnkle.x, 0.01f);
            localKnee.x = Mathf.Max(localKnee.x, 0.01f);
        }

        if (ankleT) ankleT.position = avatarRoot.TransformPoint(localAnkle);
        if (kneeT) kneeT.position = avatarRoot.TransformPoint(localKnee);
    }

    void EnforceMinLegSeparation()
    {
        Transform activeAnkle = (_activeIK == leftIK) ? leftAnkleTarget : rightAnkleTarget;
        Transform mirrorAnkle = (_mirrorIK == leftIK) ? leftAnkleTarget : rightAnkleTarget;
        Transform activeKnee = (_activeIK == leftIK) ? leftKneeHint : rightKneeHint;
        Transform mirrorKnee = (_mirrorIK == leftIK) ? leftKneeHint : rightKneeHint;

        if (activeAnkle == null || mirrorAnkle == null) return;

        Vector3 activeLocal = avatarRoot.InverseTransformPoint(activeAnkle.position);
        Vector3 mirrorLocal = avatarRoot.InverseTransformPoint(mirrorAnkle.position);

        if (Mathf.Abs(mirrorLocal.x - activeLocal.x) < baseStanceWidth)
        {
            float half = baseStanceWidth * 0.5f;
            bool activeIsLeft = (_activeIK == leftIK);

            activeLocal.x = activeIsLeft ? -half : half;
            mirrorLocal.x = activeIsLeft ? half : -half;

            activeAnkle.position = avatarRoot.TransformPoint(activeLocal);
            mirrorAnkle.position = avatarRoot.TransformPoint(mirrorLocal);

            if (activeKnee != null)
            {
                Vector3 k = avatarRoot.InverseTransformPoint(activeKnee.position);
                k.x = activeLocal.x;
                activeKnee.position = avatarRoot.TransformPoint(k);
            }
            if (mirrorKnee != null)
            {
                Vector3 k = avatarRoot.InverseTransformPoint(mirrorKnee.position);
                k.x = mirrorLocal.x;
                mirrorKnee.position = avatarRoot.TransformPoint(k);
            }
        }
    }

    void HideUpperBodyParts()
    {
        foreach (Transform t in avatarRoot.GetComponentsInChildren<Transform>())
        {
            string n = t.name.ToLower();
            if (n.Contains("head") || n.Contains("neck") ||
                n.Contains("hand") || n.Contains("finger"))
                t.localScale = Vector3.zero;
        }
    }

    void PrintBodyDebug(Vector3 kneePos, Vector3 anklePos)
    {
        string metrics = bodyEstimator != null ? bodyEstimator.GetMetricsSummary() : "";
        Debug.Log($"[body] Side:{_sideLabel} Scale:{avatarRoot.localScale.x:F2} {metrics}");
    }

    public void ResetFitter()
    {
        _hasResolvedBones = false;
        _smoothingInitialized = false;
        _hipLocked = false;
        _lockedBodyRot = Quaternion.identity;
        _activeIK = null;
        _mirrorIK = null;
        _unscaledAvatarThigh = 0f;
        avatarRoot.localScale = Vector3.zero;
       
        DisableBothIKs();

        if (bodyEstimator != null) bodyEstimator.Reset();
        if (sideSelector != null) sideSelector.ResetSide();

        Debug.Log("[body] FULL RESET");
    }
}