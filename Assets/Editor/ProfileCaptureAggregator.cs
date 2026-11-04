using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

public static class ProfileCaptureAggregator
{
    private const string CaptureDir = "ProfilerCaptures";
    private const int SkipLeadingFrames = 0;
    private const string FramePrefKey = "ProfileAggregator_DumpFrameIndex";

    [MenuItem("Tools/Profile Aggregate/Aggregate Latest Capture")]
    public static void AggregateLatest()
    {
        string path = FindLatestCapture();
        if (path == null) return;
        AggregateFile(path);
    }

    [MenuItem("Tools/Profile Aggregate/Dump Median-Cost Frame From Latest")]
    public static void DumpMedianFrameFromLatest()
    {
        string path = FindLatestCapture();
        if (path == null) return;
        DumpFrame(path, frameIndex: -1);
    }

    [MenuItem("Tools/Profile Aggregate/Dump Worst-Cost Frame From Latest")]
    public static void DumpWorstFrameFromLatest()
    {
        string path = FindLatestCapture();
        if (path == null) return;
        DumpFrame(path, frameIndex: -2);
    }

    // Comma-separated marker names whose per-frame CALL COUNT is tracked in the tick timeline.
    private static readonly string[] TimelineCallMarkers =
    {
        "Assembly-CSharp.dll!::FighterController.Update() [Invoke]",
        "Assembly-CSharp.dll!::Squad.Update() [Invoke]",
    };
    // Marker whose per-frame TOTAL MS is tracked in the tick timeline.
    private const string TimelineMsMarker = "BehaviourUpdate";

    [MenuItem("Tools/Profile Aggregate/Dump Tick Timeline From Latest")]
    public static void DumpTickTimelineFromLatest()
    {
        string path = FindLatestCapture();
        if (path == null) return;
        DumpTickTimeline(path);
    }

    public static void DumpTickTimeline(string path)
    {
        Debug.Log($"[ProfileAggregator] Loading: {path}");
        ProfilerDriver.LoadProfile(path, false);
        int firstFrame = ProfilerDriver.firstFrameIndex;
        int lastFrame = ProfilerDriver.lastFrameIndex;
        int scanLast = lastFrame > firstFrame ? lastFrame - 1 : lastFrame;

        var children = new List<int>(64);
        var stack = new Stack<int>(256);

        var sb = new StringBuilder();
        sb.AppendLine($"Capture: {path}");
        sb.AppendLine("Per-frame tick timeline. If the staggered tickers are phase-locked, FighterTicks/SquadTicks");
        sb.AppendLine("spike periodically (a few frames carry nearly all ticks, the rest carry ~0).");
        sb.AppendLine();
        sb.AppendLine("VfxInst = live VisualEffect instances (~live plasma bolts, +small constant of other VFX).");
        sb.AppendLine($"{"Frame",7} {"FrameMs",9} {"VfxInst",8} {"SquadTk",8} {"BehavMs",9} {"PhysMs",9} {"InstMs",9} {"VfxMs",8} {"RendMs",8}");
        sb.AppendLine(new string('-', 90));

        // Running stats to summarize spread.
        var fighterCounts = new List<int>(scanLast - firstFrame + 1);
        var squadCounts = new List<int>(scanLast - firstFrame + 1);

        for (int f = firstFrame; f <= scanLast; f++)
        {
            using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                f, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime, false))
            {
                if (view == null || !view.valid) continue;

                int fighterCalls = 0, squadCalls = 0, vfxInst = 0;
                float behaviourMs = 0f, physMs = 0f, instMs = 0f, vfxMs = 0f, rendMs = 0f;

                stack.Clear();
                children.Clear();
                view.GetItemChildren(view.GetRootItemID(), children);
                for (int i = 0; i < children.Count; i++) stack.Push(children[i]);
                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    string name = view.GetItemName(id);
                    float ms = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                    if (name == TimelineCallMarkers[0])
                        fighterCalls += (int)view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
                    else if (name == TimelineCallMarkers[1])
                        squadCalls += (int)view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
                    else if (name == "VisualEffect.Update")
                        vfxInst += (int)view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);
                    else if (name == TimelineMsMarker) behaviourMs += ms;
                    else if (name == "Physics2D.Simulate") physMs += ms;
                    else if (name == "Instantiate") instMs += ms;
                    else if (name == "VFX.Update") vfxMs += ms;
                    else if (name == "RenderPlayModeViewCameras") rendMs += ms;

                    children.Clear();
                    view.GetItemChildren(id, children);
                    for (int i = 0; i < children.Count; i++) stack.Push(children[i]);
                }

                fighterCounts.Add(fighterCalls);
                squadCounts.Add(squadCalls);
                sb.AppendLine($"{f,7} {view.frameTimeMs,9:F2} {vfxInst,8} {squadCalls,8} {behaviourMs,9:F2} {physMs,9:F2} {instMs,9:F2} {vfxMs,8:F2} {rendMs,8:F2}");
            }
        }

        string outPath = Path.Combine(CaptureDir, "tick_timeline.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[ProfileAggregator] Wrote {fighterCounts.Count} frames to {outPath}. " +
                  $"FighterTicks max={Max(fighterCounts)} mean={Mean(fighterCounts):F1}; " +
                  $"SquadTicks max={Max(squadCounts)} mean={Mean(squadCounts):F1}");
    }

    private static int Max(List<int> xs) { int m = 0; foreach (var x in xs) if (x > m) m = x; return m; }
    private static float Mean(List<int> xs) { if (xs.Count == 0) return 0; long s = 0; foreach (var x in xs) s += x; return (float)s / xs.Count; }

    [MenuItem("Tools/Profile Aggregate/Dump Specific Frame From Latest")]
    public static void DumpSpecificFrameFromLatest()
    {
        string path = FindLatestCapture();
        if (path == null) return;
        int frame = EditorPrefs.GetInt(FramePrefKey, 136);
        DumpFrame(path, frame);
    }

    [MenuItem("Tools/Profile Aggregate/Set Dump Frame Index")]
    public static void SetDumpFrameIndex()
    {
        int current = EditorPrefs.GetInt(FramePrefKey, 136);
        string s = EditorUtility.DisplayDialogComplex(
            "Set Dump Frame Index",
            $"Current: {current}. Edit Assets/Editor/ProfileCaptureAggregator.cs constant or use EditorPrefs.SetInt(\"{FramePrefKey}\", N).",
            "OK", "", "") == 0 ? "" : "";
    }

    private static string FindLatestCapture()
    {
        if (!Directory.Exists(CaptureDir))
        {
            Debug.LogError($"[ProfileAggregator] Directory not found: {CaptureDir}");
            return null;
        }
        var files = Directory.GetFiles(CaptureDir, "*.data");
        if (files.Length == 0)
        {
            Debug.LogError("[ProfileAggregator] No .data files in ProfilerCaptures/");
            return null;
        }
        return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
    }

    public static void AggregateFile(string path)
    {
        Debug.Log($"[ProfileAggregator] Loading: {path}");
        ProfilerDriver.LoadProfile(path, false);

        int firstFrame = ProfilerDriver.firstFrameIndex;
        int lastFrame = ProfilerDriver.lastFrameIndex;
        int totalFrames = lastFrame - firstFrame + 1;
        int analyzedFirst = firstFrame + Mathf.Min(SkipLeadingFrames, Mathf.Max(0, totalFrames - 1));
        Debug.Log($"[ProfileAggregator] Frames: {firstFrame}..{lastFrame}, analyzing {analyzedFirst}..{lastFrame}");

        var perFrameSamples = new Dictionary<string, MarkerSamples>(2048);
        var children = new List<int>(64);
        var stack = new Stack<int>(256);
        var frameTotalMs = new List<float>(totalFrames);
        int analyzedCount = 0;

        for (int f = analyzedFirst; f <= lastFrame; f++)
        {
            using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                f, 0,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnTotalTime, false))
            {
                if (view == null || !view.valid) continue;

                var thisFrame = new Dictionary<string, FrameMarker>(256);

                int rootId = view.GetRootItemID();
                stack.Clear();
                children.Clear();
                view.GetItemChildren(rootId, children);
                for (int i = 0; i < children.Count; i++) stack.Push(children[i]);

                while (stack.Count > 0)
                {
                    int id = stack.Pop();
                    string name = view.GetItemName(id);
                    if (!string.IsNullOrEmpty(name))
                    {
                        float totalMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                        float selfMs = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                        float calls = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnCalls);

                        if (thisFrame.TryGetValue(name, out var s))
                        {
                            s.totalMs += totalMs;
                            s.selfMs += selfMs;
                            s.calls += (long)calls;
                            thisFrame[name] = s;
                        }
                        else
                        {
                            thisFrame[name] = new FrameMarker { totalMs = totalMs, selfMs = selfMs, calls = (long)calls };
                        }
                    }

                    children.Clear();
                    view.GetItemChildren(id, children);
                    for (int i = 0; i < children.Count; i++) stack.Push(children[i]);
                }

                foreach (var kv in thisFrame)
                {
                    if (!perFrameSamples.TryGetValue(kv.Key, out var samples))
                    {
                        samples = new MarkerSamples();
                        perFrameSamples[kv.Key] = samples;
                    }
                    samples.totalMs.Add(kv.Value.totalMs);
                    samples.selfMs.Add(kv.Value.selfMs);
                    samples.calls.Add(kv.Value.calls);
                }

                if (thisFrame.TryGetValue("PlayerLoop", out var pl))
                    frameTotalMs.Add(pl.totalMs);
            }
            analyzedCount++;
        }

        var stats = new List<MarkerStat>(perFrameSamples.Count);
        foreach (var kv in perFrameSamples)
            stats.Add(ComputeStats(kv.Key, kv.Value));
        stats.Sort((a, b) => b.medianTotal.CompareTo(a.medianTotal));
        if (stats.Count > 250) stats.RemoveRange(250, stats.Count - 250);

        var frameTotalSorted = new List<float>(frameTotalMs);
        frameTotalSorted.Sort();
        float frameMedian = Pct(frameTotalSorted, 0.5f);
        float frameP95 = Pct(frameTotalSorted, 0.95f);
        float frameMean = frameTotalSorted.Count > 0 ? frameTotalSorted.Sum() / frameTotalSorted.Count : 0;
        float frameMin = frameTotalSorted.Count > 0 ? frameTotalSorted[0] : 0;
        float frameMax = frameTotalSorted.Count > 0 ? frameTotalSorted[frameTotalSorted.Count - 1] : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Capture: {path}");
        sb.AppendLine($"Total frames in capture: {totalFrames} ({firstFrame}..{lastFrame})");
        sb.AppendLine($"Analyzed: {analyzedCount} frames ({analyzedFirst}..{lastFrame}); skipped first {SkipLeadingFrames} to dodge spawn-spike");
        sb.AppendLine();
        sb.AppendLine("FRAME TOTAL (PlayerLoop) DISTRIBUTION:");
        sb.AppendLine($"  Min: {frameMin:F2}  Median: {frameMedian:F2}  Mean: {frameMean:F2}  P95: {frameP95:F2}  Max: {frameMax:F2}  ms");
        sb.AppendLine();
        sb.AppendLine("PER-MARKER STATS — values are PER-FRAME totals (the marker's combined time within a frame across all its calls).");
        sb.AppendLine("Sorted by MedianTotal descending. MedianTotal is the headline number — matches what you see in the profiler hierarchy for a typical frame.");
        sb.AppendLine();
        sb.AppendLine($"{"Marker",-90} {"MedianTot",10} {"MeanTot",10} {"P95Tot",10} {"MaxTot",10} {"MedianSelf",10} {"MeanCalls",10}");
        sb.AppendLine(new string('-', 160));
        foreach (var s in stats)
        {
            sb.AppendLine($"{Trunc(s.name, 88),-90} {s.medianTotal,10:F3} {s.meanTotal,10:F3} {s.p95Total,10:F3} {s.maxTotal,10:F3} {s.medianSelf,10:F3} {s.meanCalls,10:F0}");
        }

        string outPath = Path.Combine(CaptureDir, "aggregate.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[ProfileAggregator] Wrote {stats.Count} markers to {outPath}");
    }

    public static void DumpFrame(string path, int frameIndex)
    {
        Debug.Log($"[ProfileAggregator] Loading: {path}");
        ProfilerDriver.LoadProfile(path, false);

        int firstFrame = ProfilerDriver.firstFrameIndex;
        int lastFrame = ProfilerDriver.lastFrameIndex;

        if (frameIndex < 0)
        {
            bool wantWorst = frameIndex == -2;
            int totalFrames = lastFrame - firstFrame + 1;
            int analyzedFirst = firstFrame + Mathf.Min(SkipLeadingFrames, Mathf.Max(0, totalFrames - 1));
            var totals = new List<(int idx, float total)>(totalFrames);
            var scanChildren = new List<int>(64);
            var scanStack = new Stack<int>(256);
            // Skip the final frame: the last captured frame is often incomplete (0 ms).
            int scanLast = lastFrame > analyzedFirst ? lastFrame - 1 : lastFrame;
            for (int f = analyzedFirst; f <= scanLast; f++)
            {
                using (var view = ProfilerDriver.GetHierarchyFrameDataView(
                    f, 0, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime, false))
                {
                    if (view == null || !view.valid) continue;
                    // Subtract profiler-capture overhead (the screenshot taken when saving a
                    // capture can cost hundreds of ms) so an artifact frame isn't picked as worst.
                    float overhead = 0f;
                    scanStack.Clear();
                    scanChildren.Clear();
                    view.GetItemChildren(view.GetRootItemID(), scanChildren);
                    for (int i = 0; i < scanChildren.Count; i++) scanStack.Push(scanChildren[i]);
                    while (scanStack.Count > 0)
                    {
                        int id = scanStack.Pop();
                        if (view.GetItemName(id) == "Profiler.ScreenshotUpdate")
                        {
                            overhead += view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                            continue;
                        }
                        scanChildren.Clear();
                        view.GetItemChildren(id, scanChildren);
                        for (int i = 0; i < scanChildren.Count; i++) scanStack.Push(scanChildren[i]);
                    }
                    totals.Add((f, view.frameTimeMs - overhead));
                }
            }
            totals.Sort((a, b) => a.total.CompareTo(b.total));
            var pick = wantWorst ? totals[totals.Count - 1] : totals[totals.Count / 2];
            frameIndex = pick.idx;
            Debug.Log($"[ProfileAggregator] {(wantWorst ? "Worst" : "Median")}-cost frame is index {frameIndex} ({pick.total:F2} ms)");
        }

        if (frameIndex < firstFrame || frameIndex > lastFrame)
        {
            Debug.LogError($"[ProfileAggregator] Frame {frameIndex} out of range [{firstFrame}..{lastFrame}]");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Capture: {path}");
        sb.AppendLine($"Frame index: {frameIndex} (capture range {firstFrame}..{lastFrame})");
        sb.AppendLine("Hierarchy view of main thread, sorted by total time desc, depth-limited.");
        sb.AppendLine();
        sb.AppendLine($"{"Marker (indented)",-90} {"TotalMs",10} {"SelfMs",10} {"Calls",10}");
        sb.AppendLine(new string('-', 130));

        using (var view = ProfilerDriver.GetHierarchyFrameDataView(
            frameIndex, 0,
            HierarchyFrameDataView.ViewModes.Default,
            HierarchyFrameDataView.columnTotalTime, false))
        {
            if (view == null || !view.valid)
            {
                Debug.LogError($"[ProfileAggregator] Invalid view for frame {frameIndex}");
                return;
            }
            int rootId = view.GetRootItemID();
            DumpRecursive(view, rootId, 0, sb);
        }

        string outPath = Path.Combine(CaptureDir, $"frame_{frameIndex}.txt");
        File.WriteAllText(outPath, sb.ToString());
        Debug.Log($"[ProfileAggregator] Wrote frame {frameIndex} to {outPath}");
    }

    private static void DumpRecursive(HierarchyFrameDataView view, int id, int depth, StringBuilder sb)
    {
        var children = new List<int>();
        view.GetItemChildren(id, children);
        children.Sort((a, b) =>
            view.GetItemColumnDataAsFloat(b, HierarchyFrameDataView.columnTotalTime)
                .CompareTo(view.GetItemColumnDataAsFloat(a, HierarchyFrameDataView.columnTotalTime)));

        foreach (int childId in children)
        {
            string name = view.GetItemName(childId);
            float totalMs = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnTotalTime);
            float selfMs = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnSelfTime);
            float calls = view.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnCalls);

            string indent = new string(' ', depth * 2);
            string label = Trunc(indent + name, 88);
            sb.AppendLine($"{label,-90} {totalMs,10:F3} {selfMs,10:F3} {(long)calls,10}");

            if (totalMs > 0.05f && depth < 8)
                DumpRecursive(view, childId, depth + 1, sb);
        }
    }

    private static MarkerStat ComputeStats(string name, MarkerSamples samples)
    {
        var s = new MarkerStat { name = name };
        var sortedTotal = new List<float>(samples.totalMs);
        sortedTotal.Sort();
        var sortedSelf = new List<float>(samples.selfMs);
        sortedSelf.Sort();

        int n = sortedTotal.Count;
        if (n == 0) return s;

        s.medianTotal = Pct(sortedTotal, 0.5f);
        s.medianSelf = Pct(sortedSelf, 0.5f);
        s.p95Total = Pct(sortedTotal, 0.95f);
        s.maxTotal = sortedTotal[n - 1];
        s.meanTotal = sortedTotal.Sum() / n;
        s.meanCalls = (float)samples.calls.Sum() / n;
        return s;
    }

    private static float Pct(List<float> sorted, float p)
    {
        if (sorted.Count == 0) return 0;
        int idx = Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    private struct MarkerStat
    {
        public string name;
        public float medianTotal;
        public float meanTotal;
        public float p95Total;
        public float maxTotal;
        public float medianSelf;
        public float meanCalls;
    }

    private struct FrameMarker
    {
        public float totalMs;
        public float selfMs;
        public long calls;
    }

    private class MarkerSamples
    {
        public List<float> totalMs = new List<float>(64);
        public List<float> selfMs = new List<float>(64);
        public List<long> calls = new List<long>(64);
    }
}
