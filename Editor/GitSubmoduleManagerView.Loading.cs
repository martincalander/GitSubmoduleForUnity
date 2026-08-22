using System;
using UnityEditor;
using UnityEngine;

namespace MartinCalander.GitSubmoduleManager.Editor
{
    // Shared, theme-aware loading presentation for every asynchronous editor view.
    internal partial class GitSubmoduleManagerView
    {
        private const int LoadingSpinnerFrameCount = 12;
        private const double LoadingSpinnerFramesPerSecond = 10.0;

        internal static int GetLoadingSpinnerFrameIndex(double timeSeconds)
        {
            if (timeSeconds < 0.0 || double.IsNaN(timeSeconds) || double.IsInfinity(timeSeconds))
                return 0;

            double frame = Math.Floor(timeSeconds * LoadingSpinnerFramesPerSecond);
            return (int)(frame % LoadingSpinnerFrameCount);
        }

        private static Texture[] LoadLoadingSpinnerFrames(bool isProSkin)
        {
            var frames = new Texture[LoadingSpinnerFrameCount];
            string themePrefix = isProSkin ? "d_" : string.Empty;
            for (int index = 0; index < frames.Length; index++)
            {
                string frameName = $"WaitSpin{index:00}";
                GUIContent content = EditorGUIUtility.IconContent(themePrefix + frameName);
                if (content?.image == null && isProSkin)
                    content = EditorGUIUtility.IconContent(frameName);
                frames[index] = content?.image;
            }

            return frames;
        }

        private static Texture GetLoadingSpinnerTexture()
        {
            Texture[] frames = Styles.LoadingSpinnerFrames;
            if (frames == null || frames.Length == 0)
                return null;

            int frame = GetLoadingSpinnerFrameIndex(EditorApplication.timeSinceStartup);
            Texture texture = frame < frames.Length ? frames[frame] : null;
            return texture != null ? texture : frames[0];
        }

        private void DrawLoadingState(
            string title,
            string detail = "",
            float progress = -1f,
            float topSpacing = 12f)
        {
            if (topSpacing > 0f)
                EditorGUILayout.Space(topSpacing);

            const float iconSize = 16f;
            const float iconTextGap = 6f;
            Rect rowRect = GUILayoutUtility.GetRect(1f, 22f, GUILayout.ExpandWidth(true));
            var titleContent = new GUIContent(title ?? "Loading...");
            Texture spinnerTexture = GetLoadingSpinnerTexture();
            float usedIconWidth = spinnerTexture != null ? iconSize + iconTextGap : 0f;
            float maximumTextWidth = Mathf.Max(0f, rowRect.width - usedIconWidth);
            float textWidth = Mathf.Min(Styles.LoadingLabel.CalcSize(titleContent).x, maximumTextWidth);
            float groupWidth = usedIconWidth + textWidth;
            float groupX = rowRect.x + Mathf.Max(0f, (rowRect.width - groupWidth) * 0.5f);

            if (spinnerTexture != null && Event.current.type == EventType.Repaint)
            {
                var iconRect = new Rect(
                    groupX,
                    rowRect.y + (rowRect.height - iconSize) * 0.5f,
                    iconSize,
                    iconSize);
                GUI.DrawTexture(iconRect, spinnerTexture, ScaleMode.ScaleToFit, true);
            }

            var labelRect = new Rect(
                groupX + usedIconWidth,
                rowRect.y,
                textWidth,
                rowRect.height);
            GUI.Label(labelRect, titleContent, Styles.LoadingLabel);

            if (!string.IsNullOrWhiteSpace(detail))
                GUILayout.Label(detail, Styles.LoadingDetailLabel);

            if (progress >= 0f)
            {
                Rect progressRect = GUILayoutUtility.GetRect(0f, 4f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progressRect, Mathf.Clamp01(progress), string.Empty);
            }

            EditorGUILayout.Space(4f);
        }
    }
}
