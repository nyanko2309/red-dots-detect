using System.Collections;
using System.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.Android;
using Debug = UnityEngine.Debug; // Fixes the Ambiguous Reference

public class QuestStatusDebug : MonoBehaviour
{
    [Header("UI Reference")]
    public TextMeshProUGUI statusText;

    // The permission string required for Scene/Camera access
    private const string SCENE_PERMISSION = "com.oculus.permission.USE_SCENE";

    void Start()
    {
        if (statusText == null)
        {
            Debug.LogError("QuestStatusDebug: Please assign a TextMeshProUGUI component!");
            return;
        }
        statusText.text = "<b>=== SYSTEM BOOT ===</b>";
        StartCoroutine(DiagnosticRoutine());
    }

    IEnumerator DiagnosticRoutine()
    {
        // 1. Check Android Permissions
        Log("Checking Spatial Permissions...");
        if (!Permission.HasUserAuthorizedPermission(SCENE_PERMISSION))
        {
            Log("<color=yellow>Permission Missing. Requesting...</color>");
            Permission.RequestUserPermission(SCENE_PERMISSION);

            // Wait up to 5 seconds for the user to respond to the popup
            float timeout = 5f;
            while (!Permission.HasUserAuthorizedPermission(SCENE_PERMISSION) && timeout > 0)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
        }

        if (Permission.HasUserAuthorizedPermission(SCENE_PERMISSION))
            Log("<color=green>Spatial Permission: OK</color>");
        else
            Log("<color=red>Spatial Permission: NOT GRANTED</color>");

        // 2. Check OVR Manager Instance
        Log("Searching for OVRManager...");
        while (OVRManager.instance == null)
        {
            yield return new WaitForSeconds(0.5f);
        }
        Log("<color=green>OVRManager: FOUND</color>");

        // 3. Check Passthrough Capability
        // Using the instance check to ensure it's initialized
        if (OVRManager.instance.isInsightPassthroughEnabled)
            Log("<color=green>Passthrough: ENABLED</color>");
        else
            Log("<color=yellow>Passthrough: DISABLED (Check OVRManager Settings)</color>");

        // 4. THE FIX: Check if HMD is present and tracking
        Log("Waiting for HMD Tracking...");
        while (!OVRManager.isHmdPresent)
        {
            yield return new WaitForSeconds(0.5f);
        }
        Log("<color=green>HMD/Tracking: ACTIVE</color>");

        Log("\n<color=white><b>DIAGNOSTICS COMPLETE</b></color>");
        Log("Ready to sample colors.");
    }

    private void Log(string msg)
    {
        Debug.Log($"[QUEST_DEBUG] {msg}");
        if (statusText != null)
            statusText.text += "\n" + msg;
    }
}