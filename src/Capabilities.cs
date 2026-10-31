using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MonitorControl
{
    // Only direct children of vcp(...) are feature codes. Nested groups are values,
    // not additional features. Reject truncated groups instead of guessing support.
    public static class CapabilityParser
    {
        public static Dictionary<byte, List<uint>> Parse(string text)
        {
            var result = new Dictionary<byte, List<uint>>();
            if (String.IsNullOrWhiteSpace(text)) return result;
            var match = Regex.Match(text, @"\bvcp\s*\(", RegexOptions.IgnoreCase);
            if (!match.Success) return result;
            int start = match.Index + match.Length;
            int end = GroupEnd(text, start - 1);
            if (end < 0) return result;
            int pos = start;
            while (pos < end)
            {
                if (Char.IsWhiteSpace(text[pos])) { pos++; continue; }
                int tokenStart = pos;
                while (pos < end && Char.IsLetterOrDigit(text[pos])) pos++;
                if (pos == tokenStart) { pos++; continue; }
                string token = text.Substring(tokenStart, pos - tokenStart);
                byte code;
                bool valid = token.Length <= 2 && Byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code);
                code = valid ? Byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
                while (pos < end && Char.IsWhiteSpace(text[pos])) pos++;
                var values = new List<uint>();
                if (pos < end && text[pos] == '(')
                {
                    int close = GroupEnd(text, pos);
                    if (close < 0 || close > end) return new Dictionary<byte, List<uint>>();
                    string content = text.Substring(pos + 1, close - pos - 1);
                    // Nested/table values aren't a simple enum and must not become choices.
                    if (content.IndexOf('(') < 0)
                    {
                        foreach (string item in Regex.Split(content.Trim(), @"\s+"))
                        {
                            uint value;
                            if (item.Length > 0 && item.Length <= 4 && UInt32.TryParse(item, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                                if (!values.Contains(value)) values.Add(value);
                        }
                    }
                    pos = close + 1;
                }
                if (valid)
                {
                    if (!result.ContainsKey(code)) result.Add(code, values);
                    else foreach (uint value in values) if (!result[code].Contains(value)) result[code].Add(value);
                }
            }
            return result;
        }

        private static int GroupEnd(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }
    }

    public sealed class FeatureDefinition
    {
        public byte Code;
        public string Name;
        public string Hint;
        public bool Discrete;
        public bool Basic { get { return Code == 0x10 || Code == 0x12; } }
        public FeatureDefinition(byte code, string name, string hint, bool discrete)
        { Code = code; Name = name; Hint = hint; Discrete = discrete; }
    }

    public static class FeatureCatalog
    {
        public static readonly FeatureDefinition[] All = {
            new FeatureDefinition(0x10, "밝기", "화면의 백라이트 밝기를 조절합니다", false),
            new FeatureDefinition(0x12, "명암", "밝은 부분과 어두운 부분의 차이를 조절합니다", false),
            new FeatureDefinition(0x62, "스피커 볼륨", "모니터에 내장된 스피커의 볼륨입니다", false),
            new FeatureDefinition(0x60, "입력 소스", "모니터가 보고한 입력만 표시합니다", true),
            new FeatureDefinition(0x14, "색상 프리셋", "모니터가 제공하는 색상 모드를 선택합니다", true),
            new FeatureDefinition(0x16, "빨강 게인", "빨간색 채널의 강도를 조절합니다", false),
            new FeatureDefinition(0x18, "초록 게인", "초록색 채널의 강도를 조절합니다", false),
            new FeatureDefinition(0x1A, "파랑 게인", "파란색 채널의 강도를 조절합니다", false),
            new FeatureDefinition(0x87, "선명도", "윤곽선의 선명도를 조절합니다", false)
        };

        public static string ValueName(byte code, uint value)
        {
            if (code == 0x60)
            {
                switch (value)
                {
                    case 0x01: return "VGA 1"; case 0x02: return "VGA 2";
                    case 0x03: return "DVI 1"; case 0x04: return "DVI 2";
                    case 0x0F: return "DisplayPort 1"; case 0x10: return "DisplayPort 2";
                    case 0x11: return "HDMI 1"; case 0x12: return "HDMI 2";
                }
            }
            if (code == 0x14)
            {
                switch (value)
                {
                    case 0x01: return "sRGB"; case 0x02: return "모니터 기본 색상";
                    case 0x03: return "4000 K"; case 0x04: return "5000 K";
                    case 0x05: return "6500 K"; case 0x06: return "7500 K";
                    case 0x07: return "8200 K"; case 0x08: return "9300 K";
                    case 0x09: return "10000 K"; case 0x0A: return "11500 K";
                    case 0x0B: return "사용자 설정 1"; case 0x0C: return "사용자 설정 2";
                    case 0x0D: return "사용자 설정 3";
                }
            }
            // Vendor-specific enum meanings are deliberately kept as their reported code.
            return (code == 0x14 ? "프리셋 " : "입력 ") + "0x" + value.ToString("X2");
        }
    }

    public sealed class MonitorFeature
    {
        public FeatureDefinition Definition;
        public uint Minimum;
        public uint Maximum;
        public uint Current;
        public List<uint> Choices = new List<uint>();
        public string Backend = "VCP";
        public bool Writable { get { return Definition.Discrete ? Choices.Count > 0 : Maximum > Minimum; } }
        public int Percent(uint value)
        { return Maximum <= Minimum ? 0 : (int)Math.Round((Clamp(value) - (double)Minimum) * 100 / (Maximum - (double)Minimum)); }
        public uint FromPercent(double percent)
        { return (uint)Math.Round(Minimum + Math.Max(0, Math.Min(100, percent)) / 100 * (Maximum - (double)Minimum)); }
        public uint Clamp(uint value) { return Math.Max(Minimum, Math.Min(Maximum, value)); }
    }
}
