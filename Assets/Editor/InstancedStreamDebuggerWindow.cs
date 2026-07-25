using System.Collections.Generic;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FairyGUIEditor
{
    /// <summary>
    /// Live diagnostics for v4 instanced UI streams (batch 4 productization):
    /// one row per in-place stream with the counters the runtime already
    /// exposes — segments/draws, quads, runs, clips, transform slots, claimed
    /// leaves, recompile count and the two fallback reasons (non-quad topology,
    /// masked subtrees). Open via FairyGUI/Instanced UI Streams.
    /// </summary>
    public class InstancedStreamDebuggerWindow : EditorWindow
    {
        static readonly List<InstancedUIStream> sStreams = new List<InstancedUIStream>();
        Vector2 _scroll;

        [MenuItem("FairyGUI/Instanced UI Streams", false, GUIEditorToolSetPriority)]
        static void Open()
        {
            var win = GetWindow<InstancedStreamDebuggerWindow>("Instanced UI");
            win.minSize = new Vector2(720, 160);
            win.Show();
        }

        const int GUIEditorToolSetPriority = 200;

        void OnInspectorUpdate()
        {
            Repaint(); //counters move with the game; ~10Hz repaint is plenty
        }

        void OnGUI()
        {
            InstancedUIStream.GetLiveStreams(sStreams);
            EditorGUILayout.LabelField($"Live in-place streams: {sStreams.Count}"
                + $"    backend default: {(InstancedUIStream.useVertexPath ? "vertex-stream" : "buffer")}"
                + (InstancedUIStream.forceVertexPath ? " (forced)" : ""), EditorStyles.boldLabel);

            if (sStreams.Count == 0)
            {
                EditorGUILayout.HelpBox("No container has instancedRendering enabled. "
                    + "Toggle it on a Container/GComponent at runtime to see it here.", MessageType.Info);
                return;
            }

            const float W = 78;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("container", EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
                GUILayout.Label("backend", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("segments", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("quads", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("runs", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("clips", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("slots", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("claimed", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("extracts", EditorStyles.miniBoldLabel, GUILayout.Width(W));
                GUILayout.Label("fallbacks", EditorStyles.miniBoldLabel, GUILayout.Width(W + 30));
            }

            using (var sv = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = sv.scrollPosition;
                foreach (var s in sStreams)
                {
                    Container c = s.container;
                    string name = c != null && !c.isDisposed && c.gameObject != null
                        ? c.gameObject.name : "(disposed)";
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(name, EditorStyles.linkLabel, GUILayout.MinWidth(140)))
                        {
                            if (c != null && c.gameObject != null)
                                Selection.activeGameObject = c.gameObject;
                        }
                        GUILayout.Label(s.backendName, GUILayout.Width(W));
                        GUILayout.Label(s.segmentCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label(s.quadCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label(s.runCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label(s.clipEntryCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label(s.slotCount + (s.slotOverflow > 0 ? $" (+{s.slotOverflow}!)" : ""), GUILayout.Width(W));
                        GUILayout.Label(s.claimedLeafCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label(s.extractCount.ToString(), GUILayout.Width(W));
                        GUILayout.Label($"pairs {s.lastSkippedPairs} / masked {s.lastMaskedSubtrees}", GUILayout.Width(W + 30));
                    }
                }
            }

            EditorGUILayout.HelpBox("segments = draw calls contributed by streams. slots = hot interior "
                + "containers on the tier-1 matrix path; (+n!) means more movers than the 15 available slots "
                + "(they recompile per move). extracts should stay flat while the UI idles/scrolls/tweens — "
                + "a climbing count means a structure channel is firing every frame.", MessageType.None);
        }
    }
}
