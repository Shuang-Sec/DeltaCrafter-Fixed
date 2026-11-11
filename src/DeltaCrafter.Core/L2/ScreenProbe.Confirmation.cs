using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using System.Text.RegularExpressions;

namespace DeltaCrafter.Core.L2;

internal enum ConfirmationKind { Unknown, NetworkReconnect, Purchase, Abort }

/// <summary>一次现场识别的证据；按钮坐标属于该原始帧，不能跨窗口复用。</summary>
internal sealed record ConfirmationReadout(ConfirmationKind Kind, string Text, OcrLine? Button,
    CapturedFrame Frame, nint Hwnd, long CapturedAt, PixelRect Bounds, NRect Roi, double Scale);

public sealed partial class ScreenProbe
{
    internal static OcrLine? SelectConfirmationButton(ConfirmationKind kind, IReadOnlyList<OcrLine> lines)
    {
        var matches = lines.Where(line =>
        {
            string label = Normalize(line.Text);
            return kind switch
            {
                ConfirmationKind.NetworkReconnect => label == "确认重连",
                ConfirmationKind.Purchase => label is "确认" or "确定" or "确认购买"
                    || IsPurchaseAmountLabel(label),
                ConfirmationKind.Abort => label is "确认" or "确定" or "确认中止",
                _ => false
            };
        }).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// 购买确认按钮只显示绿色总金额，不带「确认」字样。2026-11-09 真实帧把它读成
    /// 「125，2彐ø」（千分位→全角逗号、3→彐、0→ø），2026-10-27 读成「52，872」。
    /// 这里只折叠已在金额按钮上实测到的形近字，并要求整段仍是金额（整数或千分位），
    /// 不从价格行里截取片段；点击点还必须落在按钮 ROI 内。
    /// </summary>
    internal static bool IsPurchaseAmountLabel(string label)
    {
        var folded = new System.Text.StringBuilder(label.Length);
        foreach (char character in label)
        {
            folded.Append(character switch
            {
                '，' or '．' or '。' or '》' or '〉' or ',' => ',',
                '彐' or 'ヨ' => '3',
                'ø' or 'Ø' or 'O' or 'o' or '口' or '囗' or '〇' or '○' => '0',
                'l' or 'I' or '|' => '1',
                'S' or 's' => '5',
                'Z' or 'z' => '2',
                _ => character,
            });
        }
        string value = folded.ToString().Trim(',');
        if (value.Length == 0) return false;
        return Regex.IsMatch(value, @"^[0-9]{1,3}(?:,[0-9]{3})+$")
            || Regex.IsMatch(value, @"^[0-9]+$");
    }

    /// <summary>仅由购买等待或明确中止流程调用；网络弹窗交回统一恢复路径。</summary>
    internal async Task<bool> TryConfirmBusinessDialogAsync(nint hwnd, ConfirmationKind expected, CancellationToken ct, bool allowClick = true)
    {
        ct.ThrowIfCancellationRequested();
        var bounds = _window.ClientRectOnScreen(hwnd);
        var frame = Capture(hwnd);
        long capturedAt = _lastCapturedAt;
        if ((await ReadNetworkReconnectFromFrameAsync(frame, ct)).Detected)
        {
            await TryHandleNetworkReconnectAsync(hwnd, ct);
            throw new StepFailedException("业务中断", "检测到网络断线，已停止当前业务，等待重新观察；不重放购买或中止。");
        }
        var result = await ReadBusinessConfirmationFromFrameAsync(frame, expected, ct);
        if (result.Button is null)
        {
            await StopOnUnknownConfirmationAsync(frame, hwnd, expected.ToString(), ct);
            return false;
        }
        if (!allowClick) return true; // 已点击仍显示时继续等待，不能穿透到背景生产页。
        var evidence = new ConfirmationReadout(expected, result.Text, result.Button, frame, hwnd, capturedAt,
            bounds, result.Roi, result.Scale);
        ClickConfirmation(evidence, ct);
        _log.Information("已发送{Kind}确认，原图坐标({X},{Y})，等待业务结果。", expected, result.Button.CenterX, result.Button.CenterY);
        return true;
    }

    /// <summary>无法分类的模态确认保留同帧证据；普通页面孤立的“确认”文字不算弹窗。</summary>
    internal async Task StopOnUnknownConfirmationAsync(CapturedFrame frame, nint hwnd, string context, CancellationToken ct)
    {
        var roi = new NRect { X=.25, Y=.25, W=.50, H=.50 };
        var read = await _ocr.ReadAsync(frame, roi, 1.5);
        ct.ThrowIfCancellationRequested();
        var labels = read.Lines.Select(l => Normalize(l.Text)).ToArray();
        bool modal = labels.Any(s => s is "确认" or "确定") && labels.Any(s => s is "取消" or "返回");
        if (!modal) return;
        var (png,dump) = await DumpFrameAsync(frame, "unknown-confirmation-" + Guid.NewGuid().ToString("N"));
        var (left,top,width,height) = PixelMapper.ToPixelRect(roi,frame.Width,frame.Height);
        var pixels = new byte[width*height*4];
        for(int row=0;row<height;row++)
            Buffer.BlockCopy(frame.Bgra,((top+row)*frame.Width+left)*4,pixels,row*width*4,width*4);
        await _capture.SavePngAsync(new CapturedFrame(width,height,pixels),Path.ChangeExtension(png,".crop.png"));
        await File.WriteAllTextAsync(Path.ChangeExtension(png,".json"),
            System.Text.Json.JsonSerializer.Serialize(new { hwnd=hwnd.ToInt64(),context,at=DateTimeOffset.Now,
                roi,scale=1.5,threshold=(int?)null,text=read.FullText,lines=read.Lines }),ct);
        throw new StepFailedException("未知确认弹窗", "当前业务无法分类此确认框，已保存现场且未点击。" + png,png,dump);
    }

    internal async Task<(string Text, OcrLine? Button, NRect Roi, double Scale)> ReadBusinessConfirmationFromFrameAsync(
        CapturedFrame frame, ConfirmationKind expected, CancellationToken ct)
    {
        var roi = expected == ConfirmationKind.Purchase ? PurchaseConfirmationDialogRoi
            : new NRect { X = .25, Y = .25, W = .50, H = .45 };
        var buttonRoi = expected == ConfirmationKind.Purchase
            ? PurchaseConfirmationButtonRoi
            : new NRect { X = .30, Y = .50, W = .40, H = .22 };
        // 1.0x 放在 2.0x 之前：实测 2x 会把千分位金额粘成「1253」，而 1x 能读到完整「125,230」。
        foreach (double scale in new[] { 1.5, 1.0, 2.0 })
        {
            ct.ThrowIfCancellationRequested();
            string text = (await _ocr.ReadAsync(frame, roi, scale)).FullText;
            string normalized = Normalize(text);
            bool recognized = expected switch
            {
                ConfirmationKind.Purchase => IsPurchaseConfirmationText(text),
                ConfirmationKind.Abort => normalized.Contains("中止")
                    && (normalized.Contains("是否") || normalized.Contains("确定要") || normalized.Contains("确认中止")),
                _ => false
            };
            if (!recognized) continue;
            var read = await _ocr.ReadAsync(frame, buttonRoi, scale);
            var button = SelectConfirmationButton(expected, read.Lines);
            if (button is null) continue;
            return (text, button, buttonRoi, scale);
        }
        return ("", null, buttonRoi, 0);
    }

    private void ClickConfirmation(ConfirmationReadout evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = FocusedClientRect(evidence.Hwnd);
        if (evidence.Kind == ConfirmationKind.Unknown || evidence.Button is not { } button
            || Environment.TickCount64 - evidence.CapturedAt > 5000
            || current.Left != evidence.Bounds.Left || current.Top != evidence.Bounds.Top
            || current.Width != evidence.Frame.Width || current.Height != evidence.Frame.Height)
            throw new StepFailedException("确认点击", "窗口位置、尺寸或识别证据发生变化，未点击。");
        var (x,y,w,h) = PixelMapper.ToPixelRect(evidence.Roi, evidence.Frame.Width, evidence.Frame.Height);
        if (!double.IsFinite(button.CenterX) || !double.IsFinite(button.CenterY)
            || button.CenterX < x || button.CenterX >= x+w || button.CenterY < y || button.CenterY >= y+h)
            throw new StepFailedException("确认点击", "识别按钮不在确认区域内，未点击。");
        ct.ThrowIfCancellationRequested();
        ClickFramePointCore(evidence.Hwnd, button.CenterX, button.CenterY,
            evidence.Kind == ConfirmationKind.NetworkReconnect);
    }
}
