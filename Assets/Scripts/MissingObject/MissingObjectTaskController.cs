using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;                   // if you use TextMeshPro
using UnityEngine;
using UnityEngine.InputSystem; // for InputAction / InputActionProperty

/// <summary>
/// Missing-Object Task (VR/Simulator-ready)
/// ----------------------------------------
/// This version removes runtime randomness and reads a fixed trial plan from
/// StreamingAssets/trials_plan.csv so every participant gets the SAME trials.
/// CSV header:
///   trial_id,set_size,change,missing_index,grid_seed
/// Example:
///   1,6,1,2,1001
/// </summary>
public class MissingObjectTaskController : MonoBehaviour
{
    [Header("Responses (Input System)")]
    [Tooltip("Button for YES (\"something is missing\"). Bind to keyboard Y, XR primary, etc.")]
    public InputActionProperty yesAction;

    [Tooltip("Button for NO (\"nothing is missing\"). Bind to keyboard N, XR secondary, etc.")]
    public InputActionProperty noAction;

    private bool _responded = false;  // lock to prevent double responses
    // ---------------------------------------------------------------------
    // Logging (optional but recommended)
    // ---------------------------------------------------------------------
    [Header("Logging")]
    [Tooltip("Drop your _Logger (GazeCsvLogger) here to record samples/events.")]
    public GazeCsvLogger logger;      // CSV logger (can be null if you don’t want logs)
    private int    trialId = 0;       // 1-based trial counter used in logs
    private double testPhaseStartTime; // used to compute RT

    // ---------------------------------------------------------------------
    // Scene references (must be assigned)
    // ---------------------------------------------------------------------
    [Header("Scene References")]
    [Tooltip("XR Origin Main Camera (or any active camera).")]
    public Camera cam;

    [Tooltip("Table transform (items are placed on top of this).")]
    public Transform table;

    [Tooltip("Small cube/sphere prefab to spawn as objects (a BLUE prefab asset).")]
    public GameObject objectPrefab;

    [Tooltip("Instruction text (TMP). Optional; can be left null.")]
    public TextMeshProUGUI instructionText;

    [Tooltip("Feedback text (TMP). Optional; can be left null.")]
    public TextMeshProUGUI feedbackText;

    // ---------------------------------------------------------------------
    // Experiment design (timing only is used now)
    // ---------------------------------------------------------------------
    [Header("Experiment (timing)")]
    [Tooltip("Break between trials (seconds).")]
    public float interTrialSecs = 3.5f;

    [Header("Timing (seconds)")]
    [Tooltip("Study display duration.")]
    public float studySecs = 1.2f;

    [Tooltip("Blank interval between Study and Test.")]
    public float retentionSecs = 0.6f;

    [Tooltip("Max time to allow a response in Test before timing out.")]
    public float testMaxSecs = 2f;

    [Tooltip("How long the TEST display remains visible.")]
    public float testDisplaySecs = 1f;

    // ---------------------------------------------------------------------
    // Appearance & geometry
    // ---------------------------------------------------------------------
    [Header("Grid & Size")]
    [Tooltip("Grid dimension (gridSize x gridSize). 4 = 4x4.")]
    public int gridSize = 4;

    [Tooltip("Spacing between grid cells (meters).")]
    public float gridSpacing = 0.06f;

    [Tooltip("Object size (meters). 0.1 = 10 cm.")]
    public float objectScale = 0.03f;

    [Header("Appearance")]
    [Tooltip("Materials used to color spawned objects (optional but recommended).")]
    public Material[] objectMaterials;

    // ---------------------------------------------------------------------
    // Gaze source (simulator today, Quest Pro later)
    // ---------------------------------------------------------------------
    [Header("Gaze source")]
    [Tooltip("Any component that implements IGazeSource (e.g., SimulatorGazeSource).")]
    public MonoBehaviour gazeSourceComponent; // will be cast to IGazeSource
    private IGazeSource _gaze;                // null if not assigned

    // ---------------------------------------------------------------------
    // Trial plan (CSV) — NEW
    // ---------------------------------------------------------------------
    [Header("Trial Plan (CSV)")]
    [Tooltip("CSV file in StreamingAssets with columns: trial_id,set_size,change,missing_index,grid_seed")]
    public string csvFileName = "trials_plan.csv";

    [Serializable]
    private class TrialSpec
    {
        public int  trialId;
        public bool change;
        public int  missingCell;      // 0..15, -1 if no disappearance

        // NEW: extra cube in TEST
        public int    addedCell;      // 0..15, -1 if no extra (but you’ll always set >=0)
        public string addedColor;     // material name for the added cube

        public string[] cellColors = new string[16];  // STUDY colors per cell
    }

    private readonly List<TrialSpec> plan = new List<TrialSpec>();
    private int planIndex = -1;

    // ---------------------------------------------------------------------
    // Internal state (per-trial)
    // ---------------------------------------------------------------------
    private readonly List<GameObject> spawned      = new List<GameObject>(); // currently visible items
    private readonly List<int>        currentSlots = new List<int>();        // chosen grid cells for this trial
    private readonly List<Material>   currentMats  = new List<Material>();   // chosen materials (colors)
    // extra cube (TEST only)
    private int      addedCellIndex = -1;
    private Material addedMat       = null;
    private int  currentN = 0;            // number of items this trial (from CSV set_size)
    private int  missingIndex = -1;       // which item is removed at Test on change trials (index into current display)
    private bool isChangeTrial = true;    // from CSV
    private bool awaitingResponse = false;
    private bool _aoiMapLoggedThisTrial = false; // ensures AOI map is logged only once (during Study)

    private IEnumerator HideAfter(float seconds) { yield return new WaitForSeconds(seconds); ClearObjects(); }

    private void OnEnable()
    {
        if (yesAction.action != null) yesAction.action.performed += OnYesPerformed;
        if (noAction.action != null)  noAction.action.performed  += OnNoPerformed;

        yesAction.action?.Enable();
        noAction.action?.Enable();
    }

    private void OnDisable()
    {
        if (yesAction.action != null) yesAction.action.performed -= OnYesPerformed;
        if (noAction.action != null)  noAction.action.performed  -= OnNoPerformed;

        yesAction.action?.Disable();
        noAction.action?.Disable();
    }

    private void OnYesPerformed(InputAction.CallbackContext ctx)
    {
        if (!awaitingResponse || _responded) return;
        _responded = true;
        logger?.LogEvent("BUTTON", "YES");
        OnResponse(true);
    }

    private void OnNoPerformed(InputAction.CallbackContext ctx)
    {
        if (!awaitingResponse || _responded) return;
        _responded = true;
        logger?.LogEvent("BUTTON", "NO");
        OnResponse(false);
    }

    // =====================================================================
    // Unity lifecycle
    // =====================================================================
    private void Awake()
    {
        if (!cam) cam = Camera.main;

        // Hard requirements: a table and an object prefab must be assigned
        if (!table)        { Debug.LogError("[MissingObjectTask] Table is not assigned.", this); enabled = false; return; }
        if (!objectPrefab) { Debug.LogError("[MissingObjectTask] Object Prefab is not assigned.", this); enabled = false; return; }

        _gaze = gazeSourceComponent as IGazeSource;

        // UI defaults
        if (feedbackText) feedbackText.gameObject.SetActive(false);
        SetInstruction("Ready…");
    }

    private void Start()
    {
        // Load fixed trial plan
        if (!LoadPlanFromCsv())
        {
            Debug.LogError($"[MissingObjectTask] Could not load {csvFileName} from StreamingAssets. " +
                           $"Please place the CSV at Assets/StreamingAssets/{csvFileName}.");
            enabled = false;
            return;
        }

        // Start CSV logging session (optional)
        if (logger != null) logger.StartSession();

        // Run the multi-trial experiment (from CSV plan)
        StartCoroutine(RunExperiment());
    }

    private void Update()
    {
        // Visualize gaze ray (cyan line in Scene view): good sanity check.
        if (_gaze != null && _gaze.TryGetGazeRay(out var ray))
            Debug.DrawRay(ray.origin, ray.direction * 3f, Color.cyan);

        // Per-frame sample logging (cheap); also feeds fixation detector + sequences.
        if (logger != null) logger.LogFrame();

        // Keyboard response (Yes/No) while we're waiting for the participant
        if (awaitingResponse)
        {
            if (Input.GetKeyDown(KeyCode.Y)) OnResponse(true);   // Yes: something is missing
            if (Input.GetKeyDown(KeyCode.N)) OnResponse(false);  // No: nothing is missing
        }
    }

    private void OnDestroy()
    {
        if (logger != null) logger.EndSession();
    }

    private int SafeParseInt(string s, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;

        if (int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int v))
            return v;

        Debug.LogWarning($"[MissingObjectTask] Could not parse int '{s}', using {defaultValue}.");
        return defaultValue; 
    }   

    // =====================================================================
    // CSV loader (deterministic trials for everyone)
    // =====================================================================
    private bool LoadPlanFromCsv()
    {
        try
        {
            TextAsset ta = Resources.Load<TextAsset>("trials_plan");
            if (ta == null)
            {
                Debug.LogError("[MissingObjectTask] Resources/trials_plan.csv not found.");
                return false;
            }

            var lines = ta.text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 1) return false;

            plan.Clear();

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var c = line.Split(',');
                // trial_id,change,missing_cell,added_cell,added_color + 16 cells
                if (c.Length < 5 + 16)
                {
                    Debug.LogWarning($"[MissingObjectTask] Skipping short line {i + 1}");
                    continue;
                }

                var spec = new TrialSpec();

                spec.trialId     = SafeParseInt(c[0], -1);
                spec.change      = SafeParseInt(c[1], 0) == 1;
                spec.missingCell = SafeParseInt(c[2], -1);

                spec.addedCell   = SafeParseInt(c[3], -1);   // -1 = no extra cube this trial
                spec.addedColor  = c[4].Trim();

                spec.cellColors = new string[16];
                for (int cell = 0; cell < 16; cell++)
                {
                    string v = c[5 + cell].Trim();
                    spec.cellColors[cell] = string.IsNullOrEmpty(v) ? null : v;
                }

                plan.Add(spec);
            }

            Debug.Log($"[MissingObjectTask] Loaded plan with {plan.Count} trials from Resources.");
            return plan.Count > 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MissingObjectTask] Failed to read CSV: {ex.Message}");
            return false;
        }
    }


    private Material FindMaterialByName(string colorName)
    {
        if (string.IsNullOrEmpty(colorName) || objectMaterials == null)
            return null;

        for (int i = 0; i < objectMaterials.Length; i++)
        {
            if (objectMaterials[i] != null &&
                string.Equals(objectMaterials[i].name, colorName, StringComparison.OrdinalIgnoreCase))
            {
                return objectMaterials[i];
            }
        }
        Debug.LogWarning($"[MissingObjectTask] No material named '{colorName}' found.");
        return null;
    }

    // =====================================================================
    // Experiment loop: use the CSV plan strictly in order
    // =====================================================================
    private IEnumerator RunExperiment()
    {
        if (logger != null) logger.LogEvent("SESSION_START", "");

        for (planIndex = 0; planIndex < plan.Count; planIndex++)
        {
            var spec   = plan[planIndex];
            trialId    = spec.trialId;

            // NEW: build currentSlots/currentMats, set currentN, isChangeTrial, missingIndex
            PrepareTrial(spec);

            // Inform the logger that a new trial starts
            if (logger != null)
            {
                logger.currentTrialId = trialId;
                logger.setSize        = currentN;
                logger.retentionS     = retentionSecs;

                logger.LogEvent(
                    "TRIAL_START",
                    $"setSize={currentN};retention={retentionSecs:F2};" +
                    $"change={(isChangeTrial ? 1 : 0)};missingCell={spec.missingCell}"
                );

                logger.LogTrialMeta(
                    trialId,
                    currentN,
                    retentionSecs,
                    isChangeTrial,
                    missingIndex,
                    gridSize,
                    gridSpacing,
                    objectScale,
                    studySecs,
                    testMaxSecs
                );
            }

            SetInstruction($"Trial {trialId}/{plan.Count}\nMemorize the objects…");

            // Run one Study→Retention→Test→Response cycle
            yield return RunTrial(spec);

            logger?.LogEvent("TRIAL_END", "");

            // Short break between trials
            yield return new WaitForSeconds(interTrialSecs);
        }

        logger?.LogEvent("SESSION_END", "");
        SetInstruction("Experiment finished.");
    }

    // =====================================================================
    // One trial (driven by a TrialSpec from CSV)
    // =====================================================================
    private IEnumerator RunTrial(TrialSpec spec)
    {
        // Pre-compute the exact slots/materials deterministically from gridSeed
        // PlanTrialFromSpec(spec);

        _aoiMapLoggedThisTrial = false;

        // ---- STUDY ----
        if (logger != null) { logger.currentPhase = "STUDY"; logger.LogEvent("PHASE_START","STUDY"); }
        SetInstruction("Memorize the objects…");
        SpawnFromPlan(); // spawns objects using the pre-planned layout & colors
        yield return new WaitForSeconds(1f);
        if (logger != null)
        {
            logger.FlushFixationAtPhaseEnd();
            logger.FlushPhaseSequences();                 // ensures sequences.csv has a STUDY row
            logger.LogEvent("PHASE_END","STUDY");
        }

        // ---- RETENTION ----
        if (logger != null) { logger.currentPhase = "RETENTION"; logger.LogEvent("PHASE_START","RETENTION"); }
        ClearObjects();
        SetInstruction("+"); // simple fixation cross
        yield return new WaitForSeconds(retentionSecs);
        if (logger != null)
        {
            // If you want a sequence row for RETENTION, you can add FlushPhaseSequences() here.
            logger.LogEvent("PHASE_END","RETENTION");
        }

        // ---- TEST ----
        if (logger != null) {
            logger.currentPhase = "TEST";
            logger.LogEvent("PHASE_START", $"TEST;change={isChangeTrial};missingIndex={missingIndex}");
        }
        testPhaseStartTime = Time.realtimeSinceStartupAsDouble;

        SetInstruction("Which one is missing?");
        SpawnFromPlan();

        if (isChangeTrial && missingIndex >= 0 && missingIndex < spawned.Count && spawned[missingIndex] != null)
        {
            Destroy(spawned[missingIndex]);
            spawned[missingIndex] = null;
            logger?.LogRemoval(trialId, missingIndex);
        }

        // --- NEW: always add an extra cube in TEST ---
        if (addedCellIndex >= 0)
        {
            Vector3 pos = SlotToWorld(addedCellIndex);

            var extra = Instantiate(objectPrefab, pos, Quaternion.identity, table);
            extra.transform.localScale = Vector3.one * objectScale;

            if (addedMat != null)
            {
                var r = extra.GetComponent<Renderer>();
                if (r != null) r.material = addedMat;
            }
            spawned.Add(extra);

            // AOI tagging
            var tag = extra.AddComponent<AoiTag>();
            tag.slotIndex = spawned.Count - 1;
            tag.aoiId     = $"r{(addedCellIndex / gridSize)}_c{(addedCellIndex % gridSize)}";
            tag.label     = addedMat ? addedMat.name : "added";

            logger?.LogEvent("ADD_CUBE", $"trial={trialId};cell={addedCellIndex};color={spec.addedColor}");
        }

        // Hide the stimulus after 0.6 s, but keep waiting for a response up to testMaxSecs
        StartCoroutine(HideAfter(testDisplaySecs));

        _responded = false;
        awaitingResponse = true;
        float t = 0f;
        while (awaitingResponse && t < testMaxSecs)
        {
            t += Time.deltaTime;
            yield return null;
        }

        // If no response, mark timeout (policy as before)
        if (awaitingResponse)
        {
            logger?.LogEvent("TIMEOUT", testMaxSecs.ToString("F2"));
            OnResponse(false);
        }

        // ensure stimulus is hidden (in case it was still visible)
        ClearObjects();
        logger?.LogEvent("PHASE_END", "TEST");

        // Inter-trial blank
        yield return new WaitForSeconds(interTrialSecs);
        SetInstruction("");
    }

    // =====================================================================
    // Trial planning from CSV: choose slots/colors deterministically
    // =====================================================================
    private void PrepareTrial(TrialSpec spec)
    {
        currentSlots.Clear();
        currentMats.Clear();

        // STUDY layout from CSV
        for (int cell = 0; cell < 16; cell++)
        {
            string colorName = spec.cellColors[cell];
            if (!string.IsNullOrEmpty(colorName))
            {
                currentSlots.Add(cell);
                currentMats.Add(FindMaterialByName(colorName));
            }
        }

        currentN      = currentSlots.Count;
        isChangeTrial = spec.change;

        // map missingCell → index in currentSlots
        if (isChangeTrial && spec.missingCell >= 0)
            missingIndex = currentSlots.IndexOf(spec.missingCell);
        else
            missingIndex = -1;

        // NEW: extra cube info
        addedCellIndex = spec.addedCell;
        addedMat       = FindMaterialByName(spec.addedColor);
    }

    // =====================================================================
    // Display: instantiate objects according to the trial plan
    // =====================================================================
    private void SpawnFromPlan()
    {
        ClearObjects();

        for (int i = 0; i < currentN; i++)
        {
            int slot = currentSlots[i];
            Vector3 pos = SlotToWorld(slot);

            var go = Instantiate(objectPrefab, pos, Quaternion.identity, table);
            go.transform.localScale = Vector3.one * objectScale;

            // Apply pre-chosen material (so no color change between Study/Test)
            var mat = (i < currentMats.Count) ? currentMats[i] : null;
            if (mat != null)
            {
                var rend = go.GetComponent<Renderer>();
                if (rend) rend.material = mat;
            }

            spawned.Add(go);

            // Tag as AOI so the logger can identify what the gaze ray hits
            var tag = go.AddComponent<AoiTag>();
            tag.slotIndex = i;
            tag.aoiId = $"r{(currentSlots[i] / gridSize)}_c{(currentSlots[i] % gridSize)}"; // row/col ID
            if (i < currentMats.Count && currentMats[i] != null) tag.label = currentMats[i].name;

            // Log AOI map once per trial (during Study) so we can reconstruct layouts
            if (!_aoiMapLoggedThisTrial && logger != null && logger.currentPhase == "STUDY")
            {
                logger.LogAoiMap(trialId, i, tag.aoiId, tag.slotIndex, tag.label, go.transform.position);
            }
        }

        if (logger != null && logger.currentPhase == "STUDY")
            _aoiMapLoggedThisTrial = true;
    }

    /// Remove all currently spawned objects (called at Retention and before respawns)
    private void ClearObjects()
    {
        for (int i = 0; i < spawned.Count; i++)
            if (spawned[i] != null) Destroy(spawned[i]);
        spawned.Clear();
    }

    /// Convert a grid slot index (0..gridSize^2-1) into a world position on the table top.
    private Vector3 SlotToWorld(int slot)
    {
        int row = slot / gridSize;
        int col = slot % gridSize;

        float x = (col - (gridSize - 1) / 2f) * gridSpacing;
        float z = (row - (gridSize - 1) / 2f) * gridSpacing;

        // Estimate table top height: assumes the table pivot is centered vertically.
        float tableTopY = table.position.y + (table.lossyScale.y * 0.5f);

        // Place object so it rests on the table with a small epsilon to avoid z-fighting.
        float y = tableTopY + (objectScale * 0.5f) + 0.005f;

        return new Vector3(table.position.x + x, y, table.position.z + z);
    }

    // =====================================================================
    // Response handling (unchanged)
    // =====================================================================
    /// <summary>
    /// saidPresent == true means participant said “Yes, something is missing”.
    /// We decide correctness by comparing against the ground truth (isChangeTrial).
    /// We log RESPONSE, CORRECT, RT, and close the TEST phase.
    /// </summary>
    public void OnResponse(bool saidPresent)
    {
        awaitingResponse = false;

        bool actuallyMissing = isChangeTrial;              // ground truth for this trial
        bool correct = (saidPresent == actuallyMissing);   // compare to participant’s answer
        double rtMs = (Time.realtimeSinceStartupAsDouble - testPhaseStartTime) * 1000.0;

        // One-row trial summary (for quick accuracy/RT analysis)
        if (logger != null)
            logger.LogTrialResult(trialId, saidPresent, correct, rtMs, isChangeTrial, missingIndex);

        // End TEST phase; also flush current fixation and write the TEST sequence row
        if (logger != null)
        {
            logger.FlushFixationAtPhaseEnd();
            logger.FlushPhaseSequences();                  // ensures sequences.csv has a TEST row
            logger.LogEvent("RESPONSE", saidPresent ? "YES" : "NO");
            logger.LogEvent("CORRECT",  correct ? "1" : "0");
            logger.LogEvent("RT_MS",    rtMs.ToString("F1", CultureInfo.InvariantCulture));
            logger.LogEvent("PHASE_END","TEST");
        }

        // Optional on-screen feedback for debugging/training
        if (feedbackText)
        {
            feedbackText.gameObject.SetActive(true);
            feedbackText.text  = correct ? "Correct" : "Wrong";
            feedbackText.color = correct ? Color.green : Color.red;
        }

        Debug.Log($"Response: {saidPresent} (correct={correct}, RT={rtMs:F1}ms, changeTrial={isChangeTrial}, missingIndex={missingIndex})");
    }

    // ---------------------------------------------------------------------
    // Small UI helpers (null-safe)
    // ---------------------------------------------------------------------
    private void SetInstruction(string msg)
    {
        if (instructionText) instructionText.text = msg;
    }

    private void SetFeedbackVisible(bool on)
    {
        if (feedbackText) feedbackText.gameObject.SetActive(on);
    }
}
