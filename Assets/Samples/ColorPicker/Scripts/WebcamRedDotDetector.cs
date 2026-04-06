// ════════════════════════════════════════════════════════
// BlobDetector.cs  — tracks one sticker, no input logic
// ════════════════════════════════════════════════════════
using Meta.XR;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Debug = UnityEngine.Debug;

[RequireComponent(typeof(LineRenderer))]
public class BlobDetector : MonoBehaviour
{
    [Header("XR")]
    public Camera vrCamera;
    public EnvironmentRaycastManager raycastManager;
    public PassthroughCameraAccess cameraAccess;

    [Header("Blob")]
    public int searchRadius = 200;

    [Header("Color Validation")]
    [Range(0.01f, 0.25f)] public float hueTolerance = 0.05f;
    [Range(0f, 0.1f)] public float hueAdaptRate = 0.05f;
    [Range(0.01f, 0.5f)] public float satTolerance = 0.20f;
    [Range(0.01f, 0.5f)] public float valTolerance = 0.20f;

    [Header("Size Validation")]
    [Range(0.1f, 1f)] public float sizeTolerance = 0.45f;

    [Header("Smoothing")]
    [Range(0.05f, 1f)] public float trackingSmooth = 0.65f;
    [Range(1f, 30f)] public float smoothFrames = 5f;

    [Header("Search (when lost)")]
    public int searchStepSize = 20;
    public int minBlobPixels = 15;
    public int searchFrameBudgetMs = 6;

    [Header("Performance")]
    [Tooltip("Raycast every N frames. 1=every frame, 2=every other, 3=every third")]
    [Range(1, 10)]
    public int worldPositionUpdateInterval = 2;

    [Tooltip("Scan at 1/N resolution. 1=full, 2=half, 4=quarter")]
    [Range(1, 8)]
    public int scanDownsample = 2;

    // ── adaptive tolerance constants ─────────────────────
    private const float HUE_TOL_TIGHT = 0.03f;
    private const float HUE_TOL_LOOSE = 0.08f;
    private const float SAT_TOL_TIGHT = 0.10f;
    private const float SAT_TOL_LOOSE = 0.25f;
    private const float VAL_TOL_TIGHT = 0.10f;
    private const float VAL_TOL_LOOSE = 0.30f;

    private float currentHueTol;
    private float currentSatTol;
    private float currentValTol;

    // ── state ────────────────────────────────────────────
    private NativeArray<Color32> pixels;
    private Vector2Int resolution;
    private bool hasPixels = false;

    public bool IsTracking { get; private set; }
    public bool IsSearching { get; private set; }
    public bool IsActive => IsTracking || IsSearching;

    private Vector2Int trackedCenter;
    private Vector2Int blobVelocity;
    private List<Vector2Int> pts = new List<Vector2Int>(1000);

    private Color32 refColor;
    private float refHue, refSat, refVal, trackedHue;
    private int referenceBlobSize = 0;
    private int framesLost = 0;
    private int consecutiveLostFrames = 0;
    private const int LOST_GRACE_FRAMES = 3;

    private int searchOffsetY = 0;

    private Vector3 lockedNormal;

    // smoothing
    private Vector3 markerVelocity = Vector3.zero;
    private Vector3 smoothedMarkerTarget;
    private bool smoothTargetSet = false;

    // ── downsampled buffer ────────────────────────────────
    private Color32[] _dsPixels;
    private Vector2Int _dsRes;

    [Header("Visuals")]
    public Transform marker;
    public float markerHeightOffset = 0.02f;

    // ── export ───────────────────────────────────────────
    // Stays valid while searching — avatar holds last position instead of freezing
    public bool HasValidLocation =>
        (IsTracking || IsSearching) && LastKnownWorldPosition != Vector3.zero;

    public Vector3 LastKnownWorldPosition { get; private set; }
    public Vector3 LastKnownNormal { get; private set; }

    // ─────────────────────────────────────────────────────
    void Start()
    {
        currentHueTol = HUE_TOL_TIGHT;
        currentSatTol = SAT_TOL_TIGHT;
        currentValTol = VAL_TOL_TIGHT;
        if (marker) marker.gameObject.SetActive(false);
    }

    public void Tick(NativeArray<Color32> framePixels, Vector2Int res)
    {
        pixels = framePixels;
        resolution = res;
        hasPixels = pixels.IsCreated && pixels.Length > 0;
        if (!hasPixels) return;

        BuildDownsampledBuffer();

        if (IsTracking) TrackBlob();
        else if (IsSearching) SearchFullscreen();

        // Update world position every N frames only
        if (marker && marker.gameObject.activeSelf && Time.frameCount % worldPositionUpdateInterval == 0)
            LastKnownWorldPosition = marker.position - LastKnownNormal * markerHeightOffset;
    }

    // ── DOWNSAMPLE ───────────────────────────────────────
    void BuildDownsampledBuffer()
    {
        int ds = Mathf.Max(1, scanDownsample);
        _dsRes = new Vector2Int(resolution.x / ds, resolution.y / ds);
        int needed = _dsRes.x * _dsRes.y;
        if (_dsPixels == null || _dsPixels.Length != needed)
            _dsPixels = new Color32[needed];

        for (int y = 0; y < _dsRes.y; y++)
            for (int x = 0; x < _dsRes.x; x++)
            {
                int r = 0, g = 0, b = 0, cnt = 0;
                for (int dy = 0; dy < ds; dy++)
                    for (int dx = 0; dx < ds; dx++)
                    {
                        int sx = x * ds + dx, sy = y * ds + dy;
                        if (sx >= resolution.x || sy >= resolution.y) continue;
                        Color32 c = pixels[sy * resolution.x + sx];
                        r += c.r; g += c.g; b += c.b; cnt++;
                    }
                _dsPixels[y * _dsRes.x + x] = new Color32((byte)(r / cnt), (byte)(g / cnt), (byte)(b / cnt), 255);
            }
    }

    Vector2Int ToDS(Vector2Int full) =>
        new Vector2Int(full.x / scanDownsample, full.y / scanDownsample);

    Vector2Int ToFull(Vector2Int ds) =>
        new Vector2Int(ds.x * scanDownsample + scanDownsample / 2,
                       ds.y * scanDownsample + scanDownsample / 2);

    Color32 GetDS(int x, int y) =>
        _dsPixels[Mathf.Clamp(y, 0, _dsRes.y - 1) * _dsRes.x + Mathf.Clamp(x, 0, _dsRes.x - 1)];

    // ── PUBLIC API ───────────────────────────────────────
    public void TriggerDetect(Vector3 worldPoint, Vector3 normal)
    {
        Vector3 vp = cameraAccess.WorldToViewportPoint(worldPoint);
        if (vp.x < 0 || vp.x > 1 || vp.y < 0 || vp.y > 1)
        { Debug.Log($"[{name}] out of viewport"); return; }

        int cx = Mathf.Clamp((int)(vp.x * resolution.x), 0, resolution.x - 1);
        int cy = Mathf.Clamp((int)(vp.y * resolution.y), 0, resolution.y - 1);
        DetectAtPixel(cx, cy, normal);
    }

    public void ResetDetector()
    {
        IsTracking = IsSearching = false;
        referenceBlobSize = 0;
        smoothTargetSet = false;
        markerVelocity = Vector3.zero;
        consecutiveLostFrames = 0;
        searchOffsetY = framesLost = 0;
        LastKnownWorldPosition = Vector3.zero;
        if (marker) marker.gameObject.SetActive(false);
        Debug.Log($"[{name}] Reset");
    }

    // ── DETECT ───────────────────────────────────────────
    void DetectAtPixel(int cx, int cy, Vector3 normal)
    {
        Color32 centerCol = pixels[cy * resolution.x + cx];
        Color.RGBToHSV(centerCol, out float h, out float s, out float v);
        if (s < 0.30f || v < 0.15f) { Debug.Log($"[{name}] Bad color s:{s:F2} v:{v:F2}"); return; }

        refHue = h; refSat = s; refVal = v; trackedHue = h;

        int sampleRadius = 40;
        float sinSum = 0, cosSum = 0, satSum = 0, valSum = 0;
        int sampleCount = 0;

        for (int dy = -sampleRadius; dy <= sampleRadius; dy += 3)
            for (int dx = -sampleRadius; dx <= sampleRadius; dx += 3)
            {
                int px = Mathf.Clamp(cx + dx, 0, resolution.x - 1);
                int py = Mathf.Clamp(cy + dy, 0, resolution.y - 1);
                Color32 c = pixels[py * resolution.x + px];
                Color.RGBToHSV(c, out float ph, out float ps, out float pv);
                if (ps < 0.30f || pv < 0.15f) continue;
                float diff = Mathf.Abs(ph - h);
                if (Mathf.Min(diff, 1f - diff) > 0.08f) continue;
                sinSum += Mathf.Sin(ph * Mathf.PI * 2f);
                cosSum += Mathf.Cos(ph * Mathf.PI * 2f);
                satSum += ps; valSum += pv; sampleCount++;
            }

        if (sampleCount > 5)
        {
            float angle = Mathf.Atan2(sinSum / sampleCount, cosSum / sampleCount);
            h = (angle / (Mathf.PI * 2f) + 1f) % 1f;
            s = satSum / sampleCount;
            v = valSum / sampleCount;
        }

        refHue = h; refSat = s; refVal = v; trackedHue = h;
        refColor = centerCol;

        trackedCenter = new Vector2Int(cx, cy);
        blobVelocity = Vector2Int.zero;
        framesLost = consecutiveLostFrames = 0;

        referenceBlobSize = CountBlobPixels(cx, cy, h);
        if (referenceBlobSize < minBlobPixels)
        { Debug.Log($"[{name}] Too small:{referenceBlobSize}"); return; }

        lockedNormal = normal;
        LastKnownNormal = normal;
        smoothTargetSet = false;
        markerVelocity = Vector3.zero;
        IsTracking = true;
        IsSearching = false;
        if (marker) marker.gameObject.SetActive(true);

        Debug.Log($"[{name}] hue:{h:F2} sat:{s:F2} val:{v:F2} size:{referenceBlobSize}px");
    }

    // ── TRACK ────────────────────────────────────────────
    void TrackBlob()
    {
        float speed = new Vector2(blobVelocity.x, blobVelocity.y).magnitude;
        float speedFactor = Mathf.Clamp01(speed / 30f);

        currentHueTol = Mathf.Lerp(HUE_TOL_TIGHT, HUE_TOL_LOOSE, speedFactor);
        currentSatTol = Mathf.Lerp(SAT_TOL_TIGHT, SAT_TOL_LOOSE, speedFactor);
        currentValTol = Mathf.Lerp(VAL_TOL_TIGHT, VAL_TOL_LOOSE, speedFactor);

        Vector2Int dsCenter = ToDS(trackedCenter);
        Vector2Int dsVel = new Vector2Int(blobVelocity.x / scanDownsample, blobVelocity.y / scanDownsample);
        Vector2Int dsPredicted = new Vector2Int(
            Mathf.Clamp(dsCenter.x + dsVel.x, 0, _dsRes.x - 1),
            Mathf.Clamp(dsCenter.y + dsVel.y, 0, _dsRes.y - 1));

        int dsRadius = Mathf.Max(1, (searchRadius + Mathf.RoundToInt(speed * 3f)) / scanDownsample);

        pts.Clear();
        for (int dy = -dsRadius; dy <= dsRadius; dy++)
            for (int dx = -dsRadius; dx <= dsRadius; dx++)
            {
                if (dx * dx + dy * dy > dsRadius * dsRadius) continue;
                int px = dsPredicted.x + dx, py = dsPredicted.y + dy;
                if (px < 0 || py < 0 || px >= _dsRes.x || py >= _dsRes.y) continue;
                Color32 col = GetDS(px, py);
                Color.RGBToHSV(col, out float h, out float s, out float v);
                if (s < 0.30f || v < 0.15f) continue;
                if (ColorMatches(h, s, v)) pts.Add(new Vector2Int(px, py));
            }

        int dsMin = Mathf.Max(1, minBlobPixels / (scanDownsample * scanDownsample));
        if (pts.Count < dsMin)
        {
            consecutiveLostFrames++;
            if (consecutiveLostFrames >= LOST_GRACE_FRAMES)
            { consecutiveLostFrames = 0; GoSearching(); }
            return;
        }

        consecutiveLostFrames = 0;

        float sx = 0, sy = 0, totalW = 0;
        foreach (var p in pts)
        {
            float dist = Vector2Int.Distance(p, dsPredicted);
            float w = 1f / (1f + dist * 0.01f);
            sx += p.x * w; sy += p.y * w; totalW += w;
        }
        Vector2Int newDS = new Vector2Int(Mathf.RoundToInt(sx / totalW), Mathf.RoundToInt(sy / totalW));
        Vector2Int newFull = ToFull(newDS);
        Vector2Int rawVel = newFull - trackedCenter;

        blobVelocity = Vector2Int.RoundToInt(Vector2.Lerp(
            new Vector2(blobVelocity.x, blobVelocity.y),
            new Vector2(rawVel.x, rawVel.y), 0.35f));

        trackedCenter = newFull;

        float adapt = Mathf.Lerp(0.05f, 0.15f, speedFactor);
        trackedHue = Mathf.Lerp(trackedHue, GetCentroidHue(pts, true), adapt);
        refSat = Mathf.Lerp(refSat, GetCentroidSat(pts, true), adapt * 0.5f);
        refVal = Mathf.Lerp(refVal, GetCentroidVal(pts, true), adapt * 0.5f);

        if (Time.frameCount % worldPositionUpdateInterval == 0)
            UpdateWorldPosition(trackedCenter);
    }

    // ── SEARCH ───────────────────────────────────────────
    void SearchFullscreen()
    {
        framesLost++;
        Vector2Int dsCenter = ToDS(trackedCenter);
        Vector2Int dsPredicted = new Vector2Int(
            Mathf.Clamp(dsCenter.x, 0, _dsRes.x - 1),
            Mathf.Clamp(dsCenter.y, 0, _dsRes.y - 1));

        int dsStep = Mathf.Max(1, searchStepSize / scanDownsample);
        int maxR = Mathf.Max(_dsRes.x, _dsRes.y);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int r = searchOffsetY; r <= maxR; r += dsStep)
        {
            if (sw.ElapsedMilliseconds >= searchFrameBudgetMs) { searchOffsetY = r; return; }

            for (int angle = 0; angle < 360; angle += 10)
            {
                float rad = angle * Mathf.Deg2Rad;
                int sx = dsPredicted.x + (int)(Mathf.Cos(rad) * r);
                int sy = dsPredicted.y + (int)(Mathf.Sin(rad) * r);
                if (sx < 0 || sy < 0 || sx >= _dsRes.x || sy >= _dsRes.y) continue;

                Color32 col = GetDS(sx, sy);
                if (!FastColorMatch(col)) continue;

                Vector2Int seed = ToFull(new Vector2Int(sx, sy));
                Vector2Int refined = RefineBlobCenter(seed);
                if (refined == Vector2Int.zero) continue;

                searchOffsetY = 0;
                Debug.Log($"[{name}] Reacquired at {refined}");
                DetectAtPixel(refined.x, refined.y, LastKnownNormal);
                return;
            }
        }
        searchOffsetY = 0;
    }

    // ── HELPERS ──────────────────────────────────────────
    void GoSearching()
    {
        IsTracking = false;
        IsSearching = true;
        framesLost = searchOffsetY = 0;
        Debug.Log($"[{name}] Lost → searching");
    }

    bool FastColorMatch(Color32 c)
    {
        int dr = c.r - refColor.r, dg = c.g - refColor.g, db = c.b - refColor.b;
        return dr * dr + dg * dg + db * db < 2500;
    }

    bool ColorMatches(float h, float s, float v)
    {
        float hueDiff = Mathf.Min(Mathf.Abs(h - trackedHue), 1f - Mathf.Abs(h - trackedHue));
        if (hueDiff > currentHueTol) return false;
        if (Mathf.Abs(s - refSat) > currentSatTol) return false;
        if (Mathf.Abs(v - refVal) > currentValTol) return false;
        return true;
    }

    int CountBlobPixels(int cx, int cy, float hue)
    {
        int count = 0;
        for (int dy = -searchRadius; dy <= searchRadius; dy += 3)
            for (int dx = -searchRadius; dx <= searchRadius; dx += 3)
            {
                if (dx * dx + dy * dy > searchRadius * searchRadius) continue;
                int px = cx + dx, py = cy + dy;
                if (px < 0 || py < 0 || px >= resolution.x || py >= resolution.y) continue;
                Color32 col = pixels[py * resolution.x + px];
                Color.RGBToHSV(col, out float h, out float s, out float v);
                if (s < 0.30f || v < 0.15f) continue;
                float diff = Mathf.Abs(h - hue);
                if (Mathf.Min(diff, 1f - diff) < hueTolerance) count++;
            }
        return count;
    }

    float GetCentroidHue(List<Vector2Int> pts, bool isDS = false)
    {
        float sinSum = 0, cosSum = 0;
        foreach (var p in pts)
        {
            Color32 c = isDS ? GetDS(p.x, p.y) : pixels[p.y * resolution.x + p.x];
            Color.RGBToHSV(c, out float h, out _, out _);
            sinSum += Mathf.Sin(h * Mathf.PI * 2f);
            cosSum += Mathf.Cos(h * Mathf.PI * 2f);
        }
        float angle = Mathf.Atan2(sinSum / pts.Count, cosSum / pts.Count);
        return (angle / (Mathf.PI * 2f) + 1f) % 1f;
    }

    float GetCentroidSat(List<Vector2Int> pts, bool isDS = false)
    {
        float sum = 0;
        foreach (var p in pts) { Color32 c = isDS ? GetDS(p.x, p.y) : pixels[p.y * resolution.x + p.x]; Color.RGBToHSV(c, out _, out float s, out _); sum += s; }
        return sum / pts.Count;
    }

    float GetCentroidVal(List<Vector2Int> pts, bool isDS = false)
    {
        float sum = 0;
        foreach (var p in pts) { Color32 c = isDS ? GetDS(p.x, p.y) : pixels[p.y * resolution.x + p.x]; Color.RGBToHSV(c, out _, out _, out float v); sum += v; }
        return sum / pts.Count;
    }

    Vector2Int RefineBlobCenter(Vector2Int seed)
    {
        var localPts = new List<Vector2Int>(300);
        int r = 80;
        for (int dy = -r; dy <= r; dy += 4)
            for (int dx = -r; dx <= r; dx += 4)
            {
                int px = seed.x + dx, py = seed.y + dy;
                if (px < 0 || py < 0 || px >= resolution.x || py >= resolution.y) continue;
                Color32 c = pixels[py * resolution.x + px];
                Color.RGBToHSV(c, out float h, out float s, out float v);
                if (s < 0.30f || v < 0.15f) continue;
                if (ColorMatches(h, s, v)) localPts.Add(new Vector2Int(px, py));
            }
        if (localPts.Count < minBlobPixels) return Vector2Int.zero;
        float sx = 0, sy = 0;
        foreach (var p in localPts) { sx += p.x; sy += p.y; }
        return new Vector2Int(Mathf.RoundToInt(sx / localPts.Count), Mathf.RoundToInt(sy / localPts.Count));
    }

    void UpdateWorldPosition(Vector2Int pixel)
    {
        float nx = (float)pixel.x / resolution.x;
        float ny = (float)pixel.y / resolution.y;
        Ray ray;
        try { ray = cameraAccess.ViewportPointToRay(new Vector3(nx, ny, 0)); }
        catch { ray = vrCamera.ViewportPointToRay(new Vector3(nx, ny, 0)); }
        if (!raycastManager.Raycast(ray, out var hit)) return;
        lockedNormal = hit.normal;
        LastKnownNormal = hit.normal;
        ApplySmoothMarker(hit.point + hit.normal * markerHeightOffset);
        if (marker) marker.gameObject.SetActive(true);
    }

    void ApplySmoothMarker(Vector3 target)
    {
        if (!smoothTargetSet)
        {
            smoothedMarkerTarget = target;
            if (marker) marker.position = target;
            smoothTargetSet = true;
            return;
        }
        smoothedMarkerTarget = Vector3.Lerp(smoothedMarkerTarget, target, trackingSmooth);
        if (marker)
            marker.position = Vector3.SmoothDamp(
                marker.position, smoothedMarkerTarget,
                ref markerVelocity, smoothFrames * Time.deltaTime);
    }
}