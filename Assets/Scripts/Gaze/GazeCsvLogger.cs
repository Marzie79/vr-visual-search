using System.IO;
using System.Text;
using UnityEngine;

public class GazeCsvLogger : MonoBehaviour
{
    [Header("Inputs")]
    [Tooltip("Component that implements IGazeSource (Simulator now; headset later).")]
    public MonoBehaviour gazeSourceComponent;   // will be cast to IGazeSource
    private IGazeSource _gaze;

    [Tooltip("Camera for viewport projection. Defaults to Camera.main if left empty.")]
    public Camera cam;

    [Header("Files")]
    public string sessionFileNamePrefix = "gaze_session";

    private StreamWriter _samples;    // per-frame samples
    private StreamWriter _events;     // phase/response/etc.
    private StreamWriter _trials;     // one row per trial
    private StreamWriter _fixations;  // fixation start/end/duration per AOI
    private StreamWriter _sequences;  // AOI sequences per phase
    private bool _logging = false;

    [HideInInspector] public int    currentTrialId { get; set; } = -1;
    [HideInInspector] public string currentPhase   { get; set; } = "IDLE"; // STUDY/RETENTION/TEST
    [HideInInspector] public int    setSize        { get; set; } = 0;
    [HideInInspector] public float  retentionS     { get; set; } = 0f;

    [Header("Fixations")]
    [Tooltip("Minimum dwell on same AOI (ms) to call it a fixation.")]
    public int minFixDurMs = 100;

    private string _lastAoiId = "";
    private long   _lastAoiStartMs = -1;
    private bool   _fixActive = false;
    private string _fixAoiId = "";
    private long   _fixStartMs = -1;

    private int _frameCounter = 0;

    private class SeqSeg { public string aoi; public long start; public long end; }
    private readonly System.Collections.Generic.List<SeqSeg> _seq = new();
    private string _seqCurrentAoi = "";
    private long   _seqCurrentStart = -1;
    private string _seqPhaseAtStart = "IDLE";

    private void Awake()
    {
        if (!cam) cam = Camera.main;
        _gaze = gazeSourceComponent as IGazeSource;
    }

    public void StartSession()
    {
        var root = Application.persistentDataPath;
        var ts   = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var baseName = $"{sessionFileNamePrefix}_{ts}";

        _samples   = new StreamWriter(Path.Combine(root, baseName + "_samples.csv"),   false, Encoding.UTF8);
        _events    = new StreamWriter(Path.Combine(root, baseName + "_events.csv"),    false, Encoding.UTF8);
        _trials    = new StreamWriter(Path.Combine(root, baseName + "_trials.csv"),    false, Encoding.UTF8);
        _fixations = new StreamWriter(Path.Combine(root, baseName + "_fixations.csv"), false, Encoding.UTF8);
        _sequences = new StreamWriter(Path.Combine(root, baseName + "_sequences.csv"), false, Encoding.UTF8);

        _samples.WriteLine("time_ms,trial,phase,set_size,retention_s,aoi_id,slot_index,label," +
                           "gaze_ox,gaze_oy,gaze_oz,gaze_dx,gaze_dy,gaze_dz," +
                           "hit_x,hit_y,hit_z,dist_m,viewport_x,viewport_y");
        _events.WriteLine("time_ms,trial,event,value");
        _trials.WriteLine("trial,set_size,retention_s,change,missing_index,response,correct,rt_ms");
        _fixations.WriteLine("trial,phase,aoi_id,start_ms,end_ms,duration_ms");
        _sequences.WriteLine("trial,phase,seq,segments");

        _logging = true;
        LogEvent("SESSION_START", "");
        Debug.Log($"[GazeCsvLogger] Writing CSVs to: {root}");
        Debug.Log($"[GazeCsvLogger] Gaze source = {(_gaze != null ? _gaze.GetType().Name : "NULL")}, cam = {(cam ? cam.name : "NULL")}");
    }

    public void EndSession()
    {
        EndFixationIfActive();
        FlushPhaseSequences();

        LogEvent("SESSION_END", "");
        _logging = false;

        _samples?.Flush();   _samples?.Close();   _samples = null;
        _events?.Flush();    _events?.Close();    _events = null;
        _trials?.Flush();    _trials?.Close();    _trials = null;
        _fixations?.Flush(); _fixations?.Close(); _fixations = null;
        _sequences?.Flush(); _sequences?.Close(); _sequences = null;
    }

    public void LogEvent(string evt, string value)
    {
        if (!_logging || _events == null) return;
        long ms = NowMs();
        _events.WriteLine($"{ms},{currentTrialId},{evt},{value}");
    }

    public void LogFrame()
    {
        if (!_logging || _samples == null) return;
        if (!cam) cam = Camera.main;

        bool haveGaze = false;
        Ray ray = default;
        if (_gaze != null)
        {
            try { haveGaze = _gaze.TryGetGazeRay(out ray); }
            catch { haveGaze = false; }
        }
        if (!haveGaze)
        {
            Vector3 o = cam ? cam.transform.position : Vector3.zero;
            Vector3 d = cam ? cam.transform.forward  : Vector3.forward;
            ray = new Ray(o, d);
        }

        string aoiId = ""; int slotIdx = -1; string label = "";
        Vector3 hitPoint = Vector3.zero; float dist = -1f;

        if (Physics.Raycast(ray, out var hit, 50f))
        {
            dist = hit.distance;
            hitPoint = hit.point;

            var tag = hit.collider.GetComponentInParent<AoiTag>();
            if (tag != null) { aoiId = tag.aoiId; slotIdx = tag.slotIndex; label = tag.label; }
        }

        Vector3 refPt = (hitPoint != Vector3.zero) ? hitPoint : (ray.origin + ray.direction * 1.0f);
        Vector3 vp = cam ? cam.WorldToViewportPoint(refPt) : Vector3.zero;

        long ms = NowMs();
        _samples.WriteLine(
            $"{ms},{currentTrialId},{currentPhase},{setSize},{retentionS},{aoiId},{slotIdx},{label}," +
            $"{ray.origin.x:F5},{ray.origin.y:F5},{ray.origin.z:F5}," +
            $"{ray.direction.x:F5},{ray.direction.y:F5},{ray.direction.z:F5}," +
            $"{refPt.x:F5},{refPt.y:F5},{refPt.z:F5},{dist:F5},{vp.x:F5},{vp.y:F5}"
        );

        UpdateFixations(aoiId, ms);
        UpdateSequence(aoiId, ms);

        _frameCounter++;
        if ((_frameCounter % 30) == 0) _samples.Flush();
    }

    public void LogTrialResult(int trialId, bool saidPresent, bool correct, double rtMs,
                               bool change = false, int missingIndex = -1)
    {
        if (!_logging || _trials == null) return;
        _trials.WriteLine($"{trialId},{setSize},{retentionS},{(change?1:0)},{missingIndex}," +
                          $"{(saidPresent?1:0)},{(correct?1:0)},{rtMs:F1}");
        _trials.Flush();
    }

    private void UpdateFixations(string currentAoi, long nowMs)
    {
        if (currentAoi != _lastAoiId)
        {
            if (_lastAoiId != "")
            {
                long dwell = nowMs - _lastAoiStartMs;
                if (!_fixActive && dwell >= minFixDurMs)
                {
                    _fixActive  = true;
                    _fixAoiId   = _lastAoiId;
                    _fixStartMs = nowMs - dwell;
                }
                if (_fixActive)
                {
                    WriteFixationRow(_fixAoiId, _fixStartMs, nowMs);
                    _fixActive = false;
                }
            }
            _lastAoiId = currentAoi;
            _lastAoiStartMs = nowMs;
        }
        else
        {
            if (!_fixActive && _lastAoiId != "" && (nowMs - _lastAoiStartMs) >= minFixDurMs)
            {
                _fixActive  = true;
                _fixAoiId   = _lastAoiId;
                _fixStartMs = _lastAoiStartMs;
            }
        }
    }

    private void EndFixationIfActive()
    {
        if (_fixActive)
        {
            WriteFixationRow(_fixAoiId, _fixStartMs, NowMs());
            _fixActive = false;
        }
    }

    private void WriteFixationRow(string aoiId, long startMs, long endMs)
    {
        if (!_logging || _fixations == null) return;
        long dur = Mathf.Max(0, (int)(endMs - startMs));
        _fixations.WriteLine($"{currentTrialId},{currentPhase},{aoiId},{startMs},{endMs},{dur}");
    }

    private static long NowMs() => (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

    private void UpdateSequence(string currentAoi, long nowMs)
    {
        if (_seqPhaseAtStart != currentPhase)
        {
            FlushPhaseSequences();
            _seqPhaseAtStart = currentPhase;
        }

        bool changed = (currentAoi != _seqCurrentAoi);

        if (changed)
        {
            if (!string.IsNullOrEmpty(_seqCurrentAoi) && _seqCurrentStart >= 0)
                _seq.Add(new SeqSeg { aoi = _seqCurrentAoi, start = _seqCurrentStart, end = nowMs });

            _seqCurrentAoi   = currentAoi;
            _seqCurrentStart = string.IsNullOrEmpty(currentAoi) ? -1 : nowMs;
        }
    }

    public void FlushPhaseSequences()
    {
        if (!_logging || _sequences == null)
        {
            ResetSeqBuffers();
            return;
        }

        long nowMs = NowMs();
        if (!string.IsNullOrEmpty(_seqCurrentAoi) && _seqCurrentStart >= 0)
            _seq.Add(new SeqSeg { aoi = _seqCurrentAoi, start = _seqCurrentStart, end = nowMs });

        var seq = new StringBuilder();
        var seg = new StringBuilder();
        bool first = true;
        foreach (var s in _seq)
        {
            long dur = Mathf.Max(0, (int)(s.end - s.start));
            if (first) { seq.Append(s.aoi); first = false; }
            else       { seq.Append('>').Append(s.aoi); }
            if (seg.Length > 0) seg.Append(';');
            seg.Append(s.aoi).Append(':').Append(dur);
        }

        if (_seq.Count > 0)
        {
            _sequences.WriteLine($"{currentTrialId},{currentPhase},{seq},{seg}");
            _sequences.Flush();
        }

        ResetSeqBuffers();
    }

    private void ResetSeqBuffers()
    {
        _seq.Clear();
        _seqCurrentAoi = "";
        _seqCurrentStart = -1;
        _seqPhaseAtStart = currentPhase;
    }

    public void LogTrialMeta(int trialId, int setSize, float retentionS,
                             bool change, int missingIndex,
                             int gridSize, float gridSpacing, float objectScale,
                             float studySecs, float testMaxSecs)
    {
        LogEvent("TRIAL_META",
            $"setSize={setSize};retention={retentionS:F2};change={(change?1:0)};" +
            $"missingIndex={missingIndex};gridSize={gridSize};gridSpacing={gridSpacing:F3};" +
            $"objScale={objectScale:F3};study={studySecs:F2};testMax={testMaxSecs:F2}");
    }

    public void LogAoiMap(int trialId, int itemIndex, string aoiId, int slotIndex, string label, Vector3 worldPos)
    {
        LogEvent("AOI",
            $"idx={itemIndex};aoi={aoiId};slot={slotIndex};label={label};" +
            $"pos=({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3})");
    }

    public void LogRemoval(int trialId, int missingIndex)
    {
        LogEvent("REMOVAL", $"missingIndex={missingIndex}");
    }

    public void FlushFixationAtPhaseEnd()
    {
        EndFixationIfActive();
        _fixations?.Flush();
    }
}
