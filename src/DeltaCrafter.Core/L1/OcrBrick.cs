using System.Runtime.InteropServices.WindowsRuntime;
using DeltaCrafter.Core.L0;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace DeltaCrafter.Core.L1;

/// <summary>一行 OCR 结果。中心坐标为原始帧内的物理像素(已换算回裁剪/缩放前)。</summary>
public sealed record OcrLine(string Text, double CenterX, double CenterY);

public sealed record OcrReadout(string FullText, IReadOnlyList<OcrLine> Lines);

/// <summary>单个 OCR 词框（原始帧物理像素坐标），用于在合并行内精确定位子标签。</summary>
internal sealed record OcrWordBox(string Text, double Left, double Top, double Right, double Bottom);

internal sealed record OcrDetailedReadout(OcrReadout Readout, IReadOnlyList<IReadOnlyList<OcrWordBox>> LineWords);

/// <summary>
/// Windows 内置中文 OCR 封装。约束:
/// 1) 游戏小字号文本直接识别率差,默认放大 2 倍(最近邻)后再识别;
/// 2) OcrEngine 有最大边长限制,超限时自动降低倍率乃至降采样(全屏诊断转储场景);
/// 3) 缺中文语言包属环境错误,构造时立即抛出并给出安装指引,不降级到其他语言。
/// </summary>
public sealed class OcrBrick
{
    private readonly OcrEngine _engine;

    private OcrBrick(OcrEngine engine) => _engine = engine;

    public static OcrBrick CreateSimplifiedChinese()
    {
        var lang = new Language("zh-Hans");
        if (!OcrEngine.IsLanguageSupported(lang))
            throw new InvalidOperationException(
                "系统缺少简体中文 OCR 组件:请到 Windows 设置 → 时间和语言 → 语言和区域 → " +
                "中文(简体) → 语言选项,安装「光学字符识别」后重启本程序。");
        var engine = OcrEngine.TryCreateFromLanguage(lang)
            ?? throw new InvalidOperationException("中文 OCR 引擎初始化失败(语言包状态异常)。");
        return new OcrBrick(engine);
    }

    /// <summary>识别帧内区域文本。roi 为空表示整帧(诊断用)。</summary>
    public async Task<OcrReadout> ReadAsync(CapturedFrame frame, NRect? roi = null, double upscale = 2.0) =>
        (await ReadDetailedAsync(frame, roi, upscale)).Readout;

    /// <summary>识别帧内区域文本并保留逐词边界框，便于在相邻标签合并为同行时精确定位目标词。</summary>
    internal async Task<OcrDetailedReadout> ReadDetailedAsync(CapturedFrame frame, NRect? roi = null, double upscale = 2.0)
    {
        var (cx, cy, cw, ch) = roi is null
            ? (0, 0, frame.Width, frame.Height)
            : PixelMapper.ToPixelRect(roi, frame.Width, frame.Height);

        // 实际倍率 = 期望倍率与引擎尺寸上限的较小者;全帧转储时可能 <1(降采样)。
        double maxDim = OcrEngine.MaxImageDimension;
        double s = Math.Min(upscale, Math.Min(maxDim / cw, maxDim / ch));
        int dw = Math.Max(1, (int)(cw * s));
        int dh = Math.Max(1, (int)(ch * s));

        var resampled = NearestResample(frame, cx, cy, cw, ch, dw, dh);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            resampled.AsBuffer(), BitmapPixelFormat.Bgra8, dw, dh, BitmapAlphaMode.Ignore);
        var result = await _engine.RecognizeAsync(bitmap);

        double sx = (double)dw / cw, sy = (double)dh / ch;
        var lines = new List<OcrLine>();
        var lineWords = new List<IReadOnlyList<OcrWordBox>>();
        foreach (var line in result.Lines)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
            var words = new List<OcrWordBox>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);
                words.Add(new OcrWordBox(
                    word.Text,
                    cx + r.X / sx,
                    cy + r.Y / sy,
                    cx + (r.X + r.Width) / sx,
                    cy + (r.Y + r.Height) / sy));
            }
            if (minX > maxX) continue; // 无词的空行,丢弃
            lines.Add(new OcrLine(line.Text,
                cx + (minX + maxX) / 2 / sx,
                cy + (minY + maxY) / 2 / sy));
            lineWords.Add(words);
        }
        return new OcrDetailedReadout(
            new OcrReadout(string.Join("\n", lines.Select(l => l.Text)), lines),
            lineWords);
    }

    /// <summary>最近邻重采样(裁剪+缩放一步完成)。按 32 位像素整块拷贝并预计算 X 索引，零额外依赖。</summary>
    internal static byte[] NearestResample(CapturedFrame f, int cx, int cy, int cw, int ch, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        var srcPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(f.Bgra.AsSpan());
        var dstPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        var xMap = new int[dw];
        for (int x = 0; x < dw; x++)
            xMap[x] = cx + (int)((long)x * cw / dw);

        int stride = f.Width;
        for (int y = 0; y < dh; y++)
        {
            int sy = cy + (int)((long)y * ch / dh);
            int srcRow = sy * stride;
            int dstRow = y * dw;
            for (int x = 0; x < dw; x++)
                dstPixels[dstRow + x] = srcPixels[srcRow + xMap[x]];
        }
        return dst;
    }
}
