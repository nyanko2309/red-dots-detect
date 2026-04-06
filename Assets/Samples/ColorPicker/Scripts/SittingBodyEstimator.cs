using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Reads OVRSkeleton AI for hip position and foot rotation.
/// Overrides knee + ankle positions with accurate sticker data.
/// Drives IK target transforms directly.
/// Hip position comes from AI estimator — not HMD — so leaning doesn't affect it.
/// </summary>
public class SittingBodyEstimator : MonoBehaviour
{
    [Header("Meta Body Tracking")]
    public OVRSkeleton ovrSkeleton;

    [Header("Sticker Tracker")]
    public BodyTracker bodyTracker;

    [Header("Side")]
    public LegSideSelector sideSelector;

    [Header("IK Target Transforms")]
    public Transform leftAnkleTarget;
    public Transform leftKneeHint;
    public Transform rightAnkleTarget;
    public Transform rightKneeHint;

    [Header("Bone Name Substrings (case-insensitive, no spaces/underscores)")]
    public string leftHipBoneName = "lefthip";
    public string rightHipBoneName = "righthip";
    public string leftAnkleBoneName = "leftfootankle";
    public string rightAnkleBoneName = "rightfootankle";

    [Header("Smoothing")]
    public float smoothSpeedFast = 15f;
    public float smoothSpeedSlow = 4f;
    public float slowVelocityThreshold = 0.05f;

    // ── Public outputs ────────────────────────────────────
    public Vector3 SmoothedKnee { get; private set; }
    public Vector3 SmoothedAnkle { get; private set; }
    public Vector3 AIHipPosition { get; private set; }  // from OVRBody
    public bool HasAIHip { get; private set; }

    public float KneeFlexionAngle { get; private set; }
    public float AnkleDorsiflexion { get; private set; }
    public float AnkleVelocity { get; private set; }

    // ── Cached bones ──────────────────────────────────────
    private Transform _hipTransform;
    private Transform _ankleTransform;
    private bool _bonesSearched = false;

    // ── Smoothing state ───────────────────────────────────
    private Vector3 _smoothedKnee;
    private Vector3 _smoothedAnkle;
    private Vector3 _prevAnkle;
    private bool _initialized = false;

    private Quaternion _aiFootRotation = Quaternion.identity;
    private bool _hasAIRotation = false;

    void LateUpdate()
    {
        if (bodyTracker == null || sideSelector == null) return;
        if (!sideSelector.sideLocked) return;
        if (!bodyTracker.KneeValid || !bodyTracker.AnkleValid) return;

        // Find bones once
        if (!_bonesSearched) TryFindBones();

        // Read AI hip + foot rotation
        if (_hipTransform != null)
        {
            AIHipPosition = _hipTransform.position;
            HasAIHip = true;
        }
        if (_ankleTransform != null)
        {
            _aiFootRotation = _ankleTransform.rotation;
            _hasAIRotation = true;
        }

        // Snap on first frame
        if (!_initialized)
        {
            _smoothedKnee = bodyTracker.KneePosition;
            _smoothedAnkle = bodyTracker.AnklePosition;
            _prevAnkle = _smoothedAnkle;
            _initialized = true;
        }

        // Adaptive smoothing
        AnkleVelocity = Vector3.Distance(bodyTracker.AnklePosition, _prevAnkle) / Time.deltaTime;
        float speed = AnkleVelocity < slowVelocityThreshold ? smoothSpeedSlow : smoothSpeedFast;

        _smoothedKnee = Vector3.Lerp(_smoothedKnee, bodyTracker.KneePosition, speed * Time.deltaTime);
        _smoothedAnkle = Vector3.Lerp(_smoothedAnkle, bodyTracker.AnklePosition, speed * Time.deltaTime);
        _prevAnkle = _smoothedAnkle;

        SmoothedKnee = _smoothedKnee;
        SmoothedAnkle = _smoothedAnkle;

        DriveIKTargets();
        ComputeMetrics();
    }

    void TryFindBones()
    {
        if (ovrSkeleton == null || !ovrSkeleton.IsDataValid) return;
        var bones = ovrSkeleton.Bones;
        if (bones == null || bones.Count == 0) return;

        bool isLeft = sideSelector.currentSide == LegSideSelector.LegSide.Left;
        string hipSearch = (isLeft ? leftHipBoneName : rightHipBoneName).ToLower().Replace("_", "");
        string ankleSearch = (isLeft ? leftAnkleBoneName : rightAnkleBoneName).ToLower().Replace("_", "");

        System.Text.StringBuilder sb = new System.Text.StringBuilder("[estimator] Available bones:\n");

        foreach (var bone in bones)
        {
            if (bone.Transform == null) continue;
            string n = bone.Transform.name.ToLower().Replace(" ", "").Replace("_", "");
            sb.AppendLine($"  {bone.Transform.name}");

            if (_hipTransform == null && n.Contains(hipSearch)) _hipTransform = bone.Transform;
            if (_ankleTransform == null && n.Contains(ankleSearch)) _ankleTransform = bone.Transform;
        }

        if (_hipTransform != null && _ankleTransform != null)
        {
            _bonesSearched = true;
            Debug.Log($"[estimator] Hip: {_hipTransform.name} | Ankle: {_ankleTransform.name}");
        }
        else
        {
            Debug.Log(sb.ToString());
        }
    }

    void DriveIKTargets()
    {
        bool isLeft = sideSelector.currentSide == LegSideSelector.LegSide.Left;

        Transform ankleT = isLeft ? leftAnkleTarget : rightAnkleTarget;
        Transform kneeT = isLeft ? leftKneeHint : rightKneeHint;

        // Drive ankle target to smoothed ankle position
        if (ankleT != null)
        {
            ankleT.position = _smoothedAnkle;
            if (_hasAIRotation) ankleT.rotation = _aiFootRotation;
        }

        // Drive knee hint to smoothed knee position
        if (kneeT != null)
            kneeT.position = _smoothedKnee;
    }

    void ComputeMetrics()
    {
        Vector3 hipPos = HasAIHip ? AIHipPosition : (_smoothedKnee + Vector3.up * 0.5f);

        Vector3 thigh = _smoothedKnee - hipPos;
        Vector3 shin = _smoothedAnkle - _smoothedKnee;

        KneeFlexionAngle = (thigh.sqrMagnitude > 0.001f && shin.sqrMagnitude > 0.001f)
            ? Vector3.Angle(thigh, shin) : 0f;

        AnkleDorsiflexion = _hasAIRotation
            ? Vector3.Angle(shin.normalized, _aiFootRotation * Vector3.forward)
            : 0f;
    }

    public void Reset()
    {
        _initialized = false;
        _hasAIRotation = false;
        _bonesSearched = false;
        _hipTransform = null;
        _ankleTransform = null;
        HasAIHip = false;
        KneeFlexionAngle = 0f;
        AnkleDorsiflexion = 0f;
        AnkleVelocity = 0f;
    }

    public string GetMetricsSummary() =>
        $"Knee:{KneeFlexionAngle:F1}° Dorsi:{AnkleDorsiflexion:F1}° Speed:{AnkleVelocity:F2}m/s";
}