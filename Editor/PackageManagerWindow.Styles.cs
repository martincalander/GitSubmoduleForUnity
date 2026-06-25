using UnityEditor;
using UnityEngine;

namespace Essentials.GitPackageManager.Editor
{
    public partial class GitPackageManagerWindow
    {
        private static class Styles
        {
            public static GUIStyle HeaderLabel;
            public static GUIStyle TitleLabel;
            public static GUIStyle SubtitleLabel;
            public static GUIStyle InfoBox;
            public static GUIStyle InfoLabel;
            public static GUIStyle InfoValue;
            public static GUIStyle FooterLabel;
            public static GUIStyle LinkButton;
            public static GUIStyle SectionHeader;
            public static GUIStyle LoadingLabel;
            public static bool Initialized;

            public static void Initialize()
            {
                if (Initialized)
                    return;

                HeaderLabel = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 11,
                    padding = new RectOffset(4, 4, 4, 4)
                };

                TitleLabel = new GUIStyle(EditorStyles.largeLabel)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                    margin = new RectOffset(0, 0, 0, 4)
                };

                SubtitleLabel = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                    margin = new RectOffset(0, 0, 0, 2)
                };

                InfoBox = new GUIStyle
                {
                    padding = new RectOffset(12, 12, 10, 10),
                    margin = new RectOffset(0, 0, 8, 8)
                };
                InfoBox.normal.background = CreateColorTexture(new Color(0.22f, 0.22f, 0.22f, 1f));

                InfoLabel = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
                    alignment = TextAnchor.MiddleLeft
                };

                InfoValue = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true
                };

                FooterLabel = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 10,
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
                    padding = new RectOffset(8, 8, 4, 4)
                };

                LinkButton = new GUIStyle(EditorStyles.linkLabel)
                {
                    fontSize = 11,
                    margin = new RectOffset(0, 12, 0, 0)
                };

                SectionHeader = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 11,
                    fontStyle = FontStyle.Bold,
                    padding = new RectOffset(0, 0, 8, 4)
                };

                LoadingLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 12
                };

                Initialized = true;
            }

            private static Texture2D CreateColorTexture(Color color)
            {
                var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                texture.SetPixel(0, 0, color);
                texture.Apply();
                return texture;
            }
        }
    }
}
