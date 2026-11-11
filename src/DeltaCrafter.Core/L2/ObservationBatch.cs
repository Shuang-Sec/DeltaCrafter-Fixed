using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L2;

/// <summary>槽位未判定的原因。失败信息与运行报告据此区分，避免把“读到但解析不出”误报成“读空”。</summary>
internal enum SlotUnresolvedReason
{
    None,
    /// <summary>五遍里连一个有效字符都没读到（整卡、计时区、标题全空）。</summary>
    NoReads,
    /// <summary>读到了时间形文本（数字 + 冒号/类似分隔符），但五遍都解析不出合法倒计时。</summary>
    TimeLikeUnparsed,
    /// <summary>读到了文本，却既不能解析成倒计时、也不足以判为可领取/空闲。</summary>
    TextWithoutPhase,
    /// <summary>有有效票，但没有第二票与之相位一致（含多票互相冲突）。</summary>
    NoConsensus,
}

/// <summary>单遍观察在某一槽位上的一票。Raw 是喂给分类器的原文（计时区 + 整卡）。</summary>
internal sealed record SlotVote(FacilityObservation Observation, long CapturedAtMs, string Title, string Raw);

/// <summary>未判定工位：原因、已得到的有效票数、最后一票原文。</summary>
internal sealed record SlotUnresolved(SlotUnresolvedReason Reason, int Votes, string LastRaw);

/// <summary>某一槽位的判定结论；Agreed 非空即两票一致。</summary>
internal readonly record struct SlotResolution(FacilityObservation? Agreed, long CapturedAtMs,
    SlotUnresolvedReason Reason, int Votes, string LastRaw);

/// <summary>
/// 一次观察的完整结果。IsComplete=false 时协调器先处理已确认工位，
/// 未判定工位保留上一次运行态，绝不用空白或猜测覆盖（见 AutomationCoordinator.RunRoundAsync）。
/// 现场证据在观察结束时截取，保证失败截图确实对应这一帧。
/// </summary>
internal sealed record ObservationBatch(
    Dictionary<FacilityKey, FacilityObservation> Agreed,
    Dictionary<FacilityKey, SlotUnresolved> Unresolved,
    string DiagnosticPng,
    string DiagnosticText)
{
    public bool IsComplete => Unresolved.Count == 0;

    /// <summary>面向人的失败原因：每个未判定工位给出原因与最后一票原文（截断）。</summary>
    public string Message
    {
        get
        {
            var parts = Unresolved.Select(entry =>
            {
                string name = FacilityKeys.DisplayName(entry.Key);
                var unresolved = entry.Value;
                string detail = unresolved.Reason switch
                {
                    SlotUnresolvedReason.NoReads => "连续读空",
                    SlotUnresolvedReason.TimeLikeUnparsed => "读到时间形文本但无法解析",
                    SlotUnresolvedReason.TextWithoutPhase => "读到文本但相位不明",
                    _ => unresolved.Votes <= 1 ? "仅一票,未获第二票" : "多票冲突,未达一致",
                };
                string raw = ScreenProbe.Normalize(unresolved.LastRaw);
                if (raw.Length > 60) raw = raw[..60] + "…";
                return raw.Length > 0 ? $"{name}({detail},末次读数:{raw})" : $"{name}({detail})";
            });
            return $"连续 5 遍读取后仍有 {Unresolved.Count} 个工位无法确定状态:" +
                   string.Join("、", parts) +
                   $"。请核对 slot 区域标定与 keywords。诊断截图:{DiagnosticPng}";
        }
    }
}
