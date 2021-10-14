using System;
using UnityEngine;
using UnityEditor;

namespace UnityInternalBridge
{
    public static class SplitterGUILayoutBridge
    {
        public static void BeginHorizontalSplit(SplitterState state, params GUILayoutOption[] options)
            => SplitterGUILayout.BeginHorizontalSplit(state.state, options);

        public static void BeginHorizontalSplit(SplitterState state, GUIStyle style, params GUILayoutOption[] options)
            => SplitterGUILayout.BeginHorizontalSplit(state.state, style, options);

        public static void BeginSplit(SplitterState state, GUIStyle style, bool vertical, params GUILayoutOption[] options)
            => SplitterGUILayout.BeginSplit(state.state, style, vertical, options);

        public static void BeginVerticalSplit(SplitterState state, params GUILayoutOption[] options)
            => SplitterGUILayout.BeginVerticalSplit(state.state, options);

        public static void BeginVerticalSplit(SplitterState state, GUIStyle style, params GUILayoutOption[] options)
            => SplitterGUILayout.BeginVerticalSplit(state.state, style, options);

        public static void EndHorizontalSplit()
            => SplitterGUILayout.EndHorizontalSplit();

        public static void EndVerticalSplit()
            => SplitterGUILayout.EndVerticalSplit();
    }

    public class SplitterState
    {
        internal UnityEditor.SplitterState state;

        public SplitterState(float[] relativeSizes, int[] minSizes, int[] maxSizes)
        {
            state = new UnityEditor.SplitterState(relativeSizes, minSizes, maxSizes);
        }
    }
}
