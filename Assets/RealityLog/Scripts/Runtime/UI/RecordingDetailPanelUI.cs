#nullable enable

using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;

namespace RealityLog.UI
{
    public class RecordingDetailPanelUI : MonoBehaviour
    {
        private RenderTexture? renderTexture;
        private VideoPlayer? videoPlayer;
        private bool isPlaying;

        public event Action? OnDeleteClicked;
        public event Action? OnExportClicked;

        private static readonly Color PanelBg = new Color(0.12f, 0.12f, 0.14f, 0.95f);
        private static readonly Color SectionBg = new Color(0.16f, 0.16f, 0.18f, 1f);
        private static readonly Color GoodColor = new Color(0.2f, 0.85f, 0.3f);
        private static readonly Color WarningColor = new Color(1f, 0.8f, 0.2f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.25f, 0.2f);
        private static readonly Color TextColor = new Color(0.9f, 0.9f, 0.9f);
        private static readonly Color DimTextColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color DeleteBtnColor = new Color(0.8f, 0.2f, 0.2f);
        private static readonly Color ExportBtnColor = new Color(0.2f, 0.5f, 0.85f);

        public void Build(RecordingDetailData data, TMP_FontAsset? font)
        {
            // Set up this object as a vertical layout container
            var rt = GetComponent<RectTransform>();
            var layout = gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 8, 8);
            layout.spacing = 6;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var fitter = gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var bg = gameObject.AddComponent<Image>();
            bg.color = PanelBg;

            // 1. Health summary
            BuildHealthSection(data, font);

            // 2. Duration row
            BuildDurationRow(data, font);

            // 3. File inventory
            BuildFileInventory(data, font);

            // 4. Video preview
            BuildVideoPreview(data);

            // 5. Action buttons
            BuildActionButtons(font);
        }

        private void BuildHealthSection(RecordingDetailData data, TMP_FontAsset? font)
        {
            var section = CreateSection("HealthSection");

            Color healthColor = data.OverallHealth switch
            {
                HealthLevel.Good => GoodColor,
                HealthLevel.Warning => WarningColor,
                HealthLevel.Error => ErrorColor,
                _ => GoodColor
            };

            string healthLabel = data.OverallHealth switch
            {
                HealthLevel.Good => "GOOD",
                HealthLevel.Warning => "WARNING",
                HealthLevel.Error => "ERROR",
                _ => "UNKNOWN"
            };

            // Health header
            var headerGo = CreateText($"Health: {healthLabel}", font, 18, FontStyles.Bold, section.transform);
            var headerTmp = headerGo.GetComponent<TextMeshProUGUI>();
            if (headerTmp != null) headerTmp.color = healthColor;

            // Issue messages
            foreach (var issue in data.Issues)
            {
                var issueGo = CreateText($"  - {issue}", font, 14, FontStyles.Normal, section.transform);
                var issueTmp = issueGo.GetComponent<TextMeshProUGUI>();
                if (issueTmp != null) issueTmp.color = DimTextColor;
            }

            if (data.Issues.Count == 0)
            {
                var noIssueGo = CreateText("  No issues detected", font, 14, FontStyles.Italic, section.transform);
                var noIssueTmp = noIssueGo.GetComponent<TextMeshProUGUI>();
                if (noIssueTmp != null) noIssueTmp.color = DimTextColor;
            }
        }

        private void BuildDurationRow(RecordingDetailData data, TMP_FontAsset? font)
        {
            var row = CreateSection("DurationRow");
            CreateText($"Duration: {data.FormattedDuration}", font, 16, FontStyles.Normal, row.transform);
        }

        private void BuildFileInventory(RecordingDetailData data, TMP_FontAsset? font)
        {
            var section = CreateSection("FileInventory");
            CreateText("Files:", font, 16, FontStyles.Bold, section.transform);

            foreach (var file in data.Files)
            {
                // Horizontal row: dot + filename + size
                var rowGo = new GameObject("FileRow", typeof(RectTransform));
                rowGo.transform.SetParent(section.transform, false);

                var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 6;
                rowLayout.childForceExpandWidth = false;
                rowLayout.childForceExpandHeight = true;
                rowLayout.childControlWidth = false;
                rowLayout.childControlHeight = true;
                rowLayout.childAlignment = TextAnchor.MiddleLeft;

                var rowFitter = rowGo.AddComponent<LayoutElement>();
                rowFitter.preferredHeight = 22;

                // Health dot
                RecordingHealthIndicator.CreateDot(rowGo.transform, file.Health, 12f);

                // File name
                var nameGo = CreateText(file.FileName, font, 13, FontStyles.Normal, rowGo.transform);
                var nameLayout = nameGo.AddComponent<LayoutElement>();
                nameLayout.flexibleWidth = 1;

                // Size (right-aligned)
                var sizeGo = CreateText(file.FormattedSize, font, 13, FontStyles.Normal, rowGo.transform);
                var sizeTmp = sizeGo.GetComponent<TextMeshProUGUI>();
                if (sizeTmp != null)
                {
                    sizeTmp.alignment = TextAlignmentOptions.MidlineRight;
                    sizeTmp.color = DimTextColor;
                }
                var sizeLayout = sizeGo.AddComponent<LayoutElement>();
                sizeLayout.minWidth = 80;
            }
        }

        private void BuildVideoPreview(RecordingDetailData data)
        {
            string videoPath = Path.Combine(data.DirectoryPath, "left_camera.mp4");
            if (!File.Exists(videoPath)) return;

            var section = CreateSection("VideoPreview");

            // RenderTexture
            renderTexture = new RenderTexture(640, 360, 0);
            renderTexture.Create();

            // RawImage for preview
            var imgGo = new GameObject("VideoImage", typeof(RectTransform), typeof(RawImage));
            imgGo.transform.SetParent(section.transform, false);
            var rawImage = imgGo.GetComponent<RawImage>();
            rawImage.texture = renderTexture;

            var imgLayout = imgGo.AddComponent<LayoutElement>();
            imgLayout.preferredHeight = 180;
            imgLayout.flexibleWidth = 1;

            var imgFitter = imgGo.AddComponent<AspectRatioFitter>();
            imgFitter.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
            imgFitter.aspectRatio = 640f / 360f;

            // VideoPlayer
            videoPlayer = gameObject.AddComponent<VideoPlayer>();
            videoPlayer.playOnAwake = false;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.targetTexture = renderTexture;
            videoPlayer.url = "file://" + videoPath;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            videoPlayer.skipOnDrop = true;
            videoPlayer.isLooping = false;

            // Prepare to show first frame as thumbnail
            videoPlayer.prepareCompleted += _ =>
            {
                videoPlayer.StepForward();
            };
            videoPlayer.Prepare();

            // Play/Pause button
            var btnGo = new GameObject("PlayPauseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(section.transform, false);

            var btnImage = btnGo.GetComponent<Image>();
            btnImage.color = new Color(0.3f, 0.3f, 0.35f);

            var btnLayout = btnGo.AddComponent<LayoutElement>();
            btnLayout.preferredHeight = 36;

            var btnTextGo = new GameObject("BtnText", typeof(RectTransform), typeof(TextMeshProUGUI));
            btnTextGo.transform.SetParent(btnGo.transform, false);
            var btnTextRt = btnTextGo.GetComponent<RectTransform>();
            btnTextRt.anchorMin = Vector2.zero;
            btnTextRt.anchorMax = Vector2.one;
            btnTextRt.sizeDelta = Vector2.zero;

            var btnTmp = btnTextGo.GetComponent<TextMeshProUGUI>();
            btnTmp.text = "Play";
            btnTmp.fontSize = 16;
            btnTmp.alignment = TextAlignmentOptions.Center;
            btnTmp.color = TextColor;

            var btn = btnGo.GetComponent<Button>();
            btn.onClick.AddListener(() =>
            {
                if (videoPlayer == null) return;
                if (isPlaying)
                {
                    videoPlayer.Pause();
                    btnTmp.text = "Play";
                    isPlaying = false;
                }
                else
                {
                    videoPlayer.Play();
                    btnTmp.text = "Pause";
                    isPlaying = true;
                }
            });
        }

        private void BuildActionButtons(TMP_FontAsset? font)
        {
            var row = new GameObject("ActionButtons", typeof(RectTransform));
            row.transform.SetParent(transform, false);

            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 10;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = true;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;

            var rowElement = row.AddComponent<LayoutElement>();
            rowElement.preferredHeight = 44;

            // Delete button
            CreateActionButton("Delete", DeleteBtnColor, font, row.transform, () => OnDeleteClicked?.Invoke());

            // Export ZIP button
            CreateActionButton("Export ZIP", ExportBtnColor, font, row.transform, () => OnExportClicked?.Invoke());
        }

        private void CreateActionButton(string label, Color color, TMP_FontAsset? font, Transform parent, Action onClick)
        {
            var btnGo = new GameObject($"{label}Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(parent, false);

            var btnImage = btnGo.GetComponent<Image>();
            btnImage.color = color;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(btnGo.transform, false);
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;

            var tmp = textGo.GetComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 16;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            if (font != null) tmp.font = font;

            var btn = btnGo.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick());
        }

        private GameObject CreateSection(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);

            var img = go.GetComponent<Image>();
            img.color = SectionBg;

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 6, 6);
            layout.spacing = 3;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return go;
        }

        private static GameObject CreateText(string text, TMP_FontAsset? font, float fontSize, FontStyles style, Transform parent)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = TextColor;
            tmp.enableWordWrapping = true;
            if (font != null) tmp.font = font;

            return go;
        }

        public void Cleanup()
        {
            if (videoPlayer != null)
            {
                videoPlayer.Stop();
                Destroy(videoPlayer);
                videoPlayer = null;
            }

            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
                renderTexture = null;
            }
        }

        private void OnDestroy()
        {
            Cleanup();
        }
    }
}
