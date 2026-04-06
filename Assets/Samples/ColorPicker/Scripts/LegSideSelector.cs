using UnityEngine;
using Debug = UnityEngine.Debug;

public class LegSideSelector : MonoBehaviour
{
    [Header("Tracking Source")]
    public BodyTracker bodyTracker;
    public Transform hmdTransform;

    [Header("Mode")]
    public SelectionMode selectionMode = SelectionMode.Auto;

    [Header("Debug")]
    public bool showDebug = true;

    [Header("Runtime Output")]
    public LegSide currentSide = LegSide.Left;
    public bool sideLocked = false;

    public enum LegSide { Left, Right }
    public enum SelectionMode { Auto, ForceLeft, ForceRight }

    private Vector3? _kneeSnapshot;
    private Vector3? _ankleSnapshot;
    [Header("UI Elements")]
    public GameObject screenSplitUI;
    void Update()
    {
        if (bodyTracker == null || hmdTransform == null) return;

        // Wait until both markers confirmed
        if (bodyTracker.CurrentStep != BodyTracker.SetupStep.AllTracking)
        {
            if (showDebug && Time.frameCount % 60 == 0)
                Debug.Log("[side] Waiting for FULL setup");
            return;
        }

        // Forced modes
        if (selectionMode == SelectionMode.ForceLeft) { currentSide = LegSide.Left; sideLocked = true; return; }
        if (selectionMode == SelectionMode.ForceRight) { currentSide = LegSide.Right; sideLocked = true; return; }

        if (sideLocked) return;

        // Capture snapshots once
        if (!_kneeSnapshot.HasValue && bodyTracker.KneeValid)
        {
            _kneeSnapshot = bodyTracker.KneePosition;
            if (showDebug) Debug.Log($"[side] Knee snapshot: {_kneeSnapshot.Value}");
        }
        if (!_ankleSnapshot.HasValue && bodyTracker.AnkleValid)
        {
            _ankleSnapshot = bodyTracker.AnklePosition;
            if (showDebug) Debug.Log($"[side] Ankle snapshot: {_ankleSnapshot.Value}");
        }

        if (!_kneeSnapshot.HasValue || !_ankleSnapshot.HasValue) return;

        // Vote using world X position directly — left of world centre = left leg
        // Compare knee and ankle X to the HMD X to determine side
        int leftVotes = 0, rightVotes = 0;
        VoteByWorldX(_kneeSnapshot.Value, ref leftVotes, ref rightVotes, "KNEE");
        VoteByWorldX(_ankleSnapshot.Value, ref leftVotes, ref rightVotes, "ANKLE");

        currentSide = leftVotes > rightVotes ? LegSide.Left : LegSide.Right;
        sideLocked = true;

        if (showDebug)
            Debug.Log($"[side] LOCKED → {currentSide} (L:{leftVotes} R:{rightVotes})");

        if (screenSplitUI != null && screenSplitUI.activeSelf)
        {
            screenSplitUI.SetActive(false);
        }
    }

    // Vote based on whether marker is to the left or right of the HMD in world space
    void VoteByWorldX(Vector3 worldPos, ref int left, ref int right, string jointName)
    {
        // Use HMD-relative position but only care about the X axis
        Vector3 relative = hmdTransform.InverseTransformPoint(worldPos);

        if (showDebug)
            Debug.Log($"[side] {jointName} world:{worldPos:F2} relative:{relative:F2}");

        // Negative relative X = to the left of the HMD = left leg
        if (relative.x < 0)
        {
            left++;
            if (showDebug) Debug.Log($"[side] {jointName} → LEFT  (rel.x:{relative.x:F3})");
        }
        else
        {
            right++;
            if (showDebug) Debug.Log($"[side] {jointName} → RIGHT (rel.x:{relative.x:F3})");
        }
    }

    public void ResetSide()
    {
        if (screenSplitUI != null && !screenSplitUI.activeSelf)
        {
            screenSplitUI.SetActive(true);
        }
        sideLocked = false;
        currentSide = LegSide.Left;
        _kneeSnapshot = null;
        _ankleSnapshot = null;
        if (showDebug) Debug.Log("[side] Reset complete");
    }
}