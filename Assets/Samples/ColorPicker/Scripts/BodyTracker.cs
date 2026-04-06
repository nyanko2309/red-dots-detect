using Meta.XR;
using UnityEngine;
using Unity.Collections;
using Debug = UnityEngine.Debug;

public class BodyTracker : MonoBehaviour
{
    [Header("XR")]
    public Transform rayOrigin;
    public Camera vrCamera;
    public EnvironmentRaycastManager raycastManager;
    public PassthroughCameraAccess cameraAccess;

    [Header("Detectors — assign in Inspector")]
    public BlobDetector kneeDetector;
    public BlobDetector ankleDetector;

    [Header("Fitter — assign in Inspector")]
    public LegRootFitter legRootFitter;

    [Header("Laser")]
    public LineRenderer laser;
    public float laserLength = 5f;

    public enum SetupStep { Idle, WaitingKnee, WaitingAnkle, AllTracking }
    public SetupStep CurrentStep { get; private set; } = SetupStep.Idle;

    private bool _triggerWasDown = false;

    public Vector3 KneePosition => kneeDetector.LastKnownWorldPosition;
    public Vector3 AnklePosition => ankleDetector.LastKnownWorldPosition;

    public bool KneeValid => kneeDetector.HasValidLocation;
    public bool AnkleValid => ankleDetector.HasValidLocation;

    void Start()
    {
        if (laser) laser.positionCount = 2;
        CurrentStep = SetupStep.Idle;
        Debug.Log("[start] Press trigger to detect KNEE sticker");
    }

    void Update()
    {
        if (!cameraAccess || !cameraAccess.IsPlaying) return;

        NativeArray<Color32> pixels = cameraAccess.GetColors();
        Vector2Int res = cameraAccess.CurrentResolution;
        if (!pixels.IsCreated || pixels.Length == 0) return;

        // Always tick both so they are ready the moment user presses
        kneeDetector.Tick(pixels, res);
        ankleDetector.Tick(pixels, res);

        // Laser
        Ray ray = new Ray(rayOrigin.position, rayOrigin.forward);
        bool hitSurface = raycastManager.Raycast(ray, out var hit);
        Vector3 laserEnd = hitSurface ? hit.point : ray.origin + ray.direction * laserLength;
        if (laser)
        {
            laser.SetPosition(0, ray.origin);
            laser.SetPosition(1, laserEnd);
        }

        // Trigger
        float trigger = Mathf.Max(
            OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger),
            OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger));

        bool triggerDown = trigger > 0.8f;
        if (triggerDown && !_triggerWasDown)
        {
            HandleButton(hitSurface, hit);
            Debug.Log("[button] pressed");
        }
        _triggerWasDown = triggerDown;
    }

    void HandleButton(bool hitSurface, EnvironmentRaycastHit hit)
    {
        // Press 3 — full reset
        if (CurrentStep == SetupStep.AllTracking)
        {
            ResetAll();
            return;
        }

        if (!hitSurface)
        {
            Debug.Log("[BodyTracker] No surface hit — point at sticker");
            return;
        }

        // Press 1 — detect knee
        if (CurrentStep == SetupStep.Idle)
        {
            CurrentStep = SetupStep.WaitingKnee;
            kneeDetector.TriggerDetect(hit.point, hit.normal);
            if (kneeDetector.IsTracking)
            {
                CurrentStep = SetupStep.WaitingAnkle;
                LogStep();
            }
            else
                Debug.Log("[BodyTracker] Knee detect failed — try again");
            return;
        }

        // Press 2 — detect ankle
        if (CurrentStep == SetupStep.WaitingAnkle)
        {
            ankleDetector.TriggerDetect(hit.point, hit.normal);
            if (ankleDetector.IsTracking)
            {
                CurrentStep = SetupStep.AllTracking;
                LogStep();
            }
            else
                Debug.Log("[BodyTracker] Ankle detect failed — try again");
        }
    }

    void ResetAll()
    {
        kneeDetector.ResetDetector();
        ankleDetector.ResetDetector();
        CurrentStep = SetupStep.Idle;

        if (legRootFitter != null)
            legRootFitter.ResetFitter();

        LogStep();
    }

    void LogStep()
    {
        switch (CurrentStep)
        {
            case SetupStep.Idle: Debug.Log("▶ Press trigger to detect KNEE sticker"); break;
            case SetupStep.WaitingAnkle: Debug.Log("👆 Point at ANKLE sticker → squeeze trigger"); break;
            case SetupStep.AllTracking: Debug.Log("✅ Knee + Ankle tracking!"); break;
        }
    }
}