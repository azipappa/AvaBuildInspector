using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    /// <summary>
    /// Editorスキン（Dark / Light）に合わせたインラインスタイル用の色。
    /// 一覧行の背景・選択・ホバーはAvaBuildInspector.ussで定義する。
    /// </summary>
    internal static class AbiTheme
    {
        public static bool IsDark => EditorGUIUtility.isProSkin;

        private static Color Gray(float dark, float light)
        {
            var value = IsDark ? dark : light;
            return new Color(value, value, value);
        }

        private static Color Pick(Color dark, Color light) => IsDark ? dark : light;

        public static Color Card => Gray(0.16f, 0.84f);
        public static Color CardDeep => Gray(0.13f, 0.80f);
        public static Color Band => Gray(0.15f, 0.82f);
        public static Color Chip => Gray(0.21f, 0.88f);
        public static Color ColumnHeader => Gray(0.12f, 0.76f);
        public static Color Border => Gray(0.28f, 0.62f);
        public static Color Track => Gray(0.10f, 0.70f);
        public static Color TabIdle => Gray(0.20f, 0.80f);
        public static Color TabSelected => Pick(new Color(0.25f, 0.47f, 0.68f), new Color(0.27f, 0.50f, 0.78f));
        public static Color SubTabSelected => Pick(new Color(0.30f, 0.42f, 0.54f), new Color(0.40f, 0.58f, 0.80f));
        public static Color SelectedCard => Pick(new Color(0.22f, 0.36f, 0.49f), new Color(0.68f, 0.79f, 0.92f));
        public static Color HistoryHeader => Pick(new Color(0.13f, 0.17f, 0.22f), new Color(0.74f, 0.80f, 0.88f));
        public static Color WarningPanel => Pick(new Color(0.34f, 0.23f, 0.10f), new Color(0.98f, 0.86f, 0.66f));
        public static Color SuccessPanel => Pick(new Color(0.15f, 0.25f, 0.17f), new Color(0.78f, 0.90f, 0.79f));
        public static Color InfoPanel => Pick(new Color(0.17f, 0.22f, 0.29f), new Color(0.80f, 0.87f, 0.95f));
        public static Color BarFill => Pick(new Color(0.30f, 0.62f, 0.86f), new Color(0.20f, 0.50f, 0.80f));
        public static Color BarHighlight => Pick(new Color(1f, 0.78f, 0.30f), new Color(0.85f, 0.50f, 0.05f));
        public static Color Increase => Pick(new Color(0.91f, 0.48f, 0.24f), new Color(0.78f, 0.34f, 0.08f));
        public static Color Decrease => Pick(new Color(0.35f, 0.76f, 0.51f), new Color(0.10f, 0.55f, 0.28f));
        public static Color Neutral => Pick(new Color(0.55f, 0.58f, 0.61f), new Color(0.40f, 0.42f, 0.45f));
        public static Color Success => Pick(new Color(0.48f, 0.88f, 0.64f), new Color(0.08f, 0.52f, 0.25f));
        public static Color Failure => Pick(new Color(1f, 0.58f, 0.54f), new Color(0.75f, 0.18f, 0.15f));
        public static Color SuccessStrong => Pick(new Color(0.18f, 0.48f, 0.30f), new Color(0.22f, 0.60f, 0.36f));
        public static Color FailureStrong => Pick(new Color(0.62f, 0.25f, 0.25f), new Color(0.80f, 0.30f, 0.28f));
        public static Color SuccessBorder => Pick(new Color(0.25f, 0.65f, 0.40f), new Color(0.20f, 0.60f, 0.35f));
        public static Color FailureBorder => Pick(new Color(0.78f, 0.34f, 0.32f), new Color(0.80f, 0.30f, 0.28f));
        public static Color Warning => Pick(new Color(1f, 0.72f, 0.28f), new Color(0.70f, 0.38f, 0.0f));

        public static Color Rating(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.VeryPoor: return Pick(new Color(1f, 0.28f, 0.28f), new Color(0.80f, 0.10f, 0.10f));
                case PerformanceRating.Poor: return Pick(new Color(1f, 0.62f, 0.2f), new Color(0.80f, 0.40f, 0.0f));
                case PerformanceRating.Medium: return Pick(new Color(1f, 0.85f, 0.25f), new Color(0.60f, 0.48f, 0.0f));
                case PerformanceRating.Good: return Pick(new Color(0.48f, 0.82f, 0.36f), new Color(0.20f, 0.55f, 0.12f));
                case PerformanceRating.Excellent: return Pick(new Color(0.3f, 0.75f, 0.55f), new Color(0.05f, 0.50f, 0.35f));
                default: return Gray(0.6f, 0.45f);
            }
        }

        public static void SetRadius(IStyle style, float radius)
        {
            style.borderTopLeftRadius = radius;
            style.borderTopRightRadius = radius;
            style.borderBottomLeftRadius = radius;
            style.borderBottomRightRadius = radius;
        }
    }
}
