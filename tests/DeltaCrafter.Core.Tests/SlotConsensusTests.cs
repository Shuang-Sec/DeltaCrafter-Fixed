using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

/// <summary>
/// 用真实运行日志里的五遍序列验证归约规则：今天（2026-11-10 16:40）制药台五遍全部
/// "读到时间形文本却解析不出"，旧实现报成"始终读空"并整轮作废；新实现必须把它
/// 标成 TimeLikeUnparsed，并且不因为只有一个有效票就采信。
/// </summary>
public class SlotConsensusTests
{
    [Fact]
    public void Today_pharmacy_sequence_is_reported_as_time_like_not_empty()
    {
        var votes = Enumerable.Range(0, 5)
            .Select(i => Unknown($"0J58：0{i}\nM2肌肉注射剂．0058：0{i}", i * 800))
            .ToArray();
        var resolution = CollectFlow.ResolveSlot(votes);
        Assert.Null(resolution.Agreed);
        Assert.Equal(SlotUnresolvedReason.TimeLikeUnparsed, resolution.Reason);
        Assert.Equal(0, resolution.Votes);
        Assert.Contains("0J58：0", resolution.LastRaw);
    }

    [Fact]
    public void All_blank_sequence_is_reported_as_no_reads()
    {
        var votes = Enumerable.Range(0, 5).Select(i => Unknown(" \n", i * 800)).ToArray();
        var resolution = CollectFlow.ResolveSlot(votes);
        Assert.Null(resolution.Agreed);
        Assert.Equal(SlotUnresolvedReason.NoReads, resolution.Reason);
    }

    [Fact]
    public void Text_without_any_phase_evidence_is_reported_separately()
    {
        var votes = Enumerable.Range(0, 5).Select(i => Unknown("·\n", i * 800)).ToArray();
        var resolution = CollectFlow.ResolveSlot(votes);
        Assert.Equal(SlotUnresolvedReason.TextWithoutPhase, resolution.Reason);
    }

    [Fact]
    public void Single_crafting_vote_is_not_enough()
    {
        var votes = new[]
        {
            Crafting(58, 9, 0),
            Unknown("0J58：08\nM2肌肉注射剂", 800),
            Unknown("", 1600),
            Unknown("", 2400),
            Unknown("", 3200),
        };
        var resolution = CollectFlow.ResolveSlot(votes);
        Assert.Null(resolution.Agreed);
        Assert.Equal(SlotUnresolvedReason.NoConsensus, resolution.Reason);
        Assert.Equal(1, resolution.Votes);
    }

    [Fact]
    public void Two_monotonic_crafting_votes_agree()
    {
        var resolution = CollectFlow.ResolveSlot([Crafting(58, 9, 0), Crafting(58, 7, 800)]);
        Assert.NotNull(resolution.Agreed);
        Assert.Equal(FacilityPhase.Crafting, resolution.Agreed!.Phase);
        Assert.Equal(new TimeSpan(0, 58, 7), resolution.Agreed.Remaining);
    }

    [Fact]
    public void Two_crafting_votes_that_do_not_decrease_do_not_agree()
    {
        var resolution = CollectFlow.ResolveSlot([Crafting(58, 9, 0), Crafting(58, 30, 800)]);
        Assert.Null(resolution.Agreed);
        Assert.Equal(SlotUnresolvedReason.NoConsensus, resolution.Reason);
    }

    [Fact]
    public void Ready_negative_evidence_never_overrides_a_crafting_vote()
    {
        var votes = new[]
        {
            Crafting(58, 9, 0),
            new SlotVote(new FacilityObservation(FacilityPhase.ReadyToCollect, null, "M2肌肉注射剂", "M2肌肉注射剂"),
                800, "M2肌肉注射剂", "M2肌肉注射剂"),
        };
        var resolution = CollectFlow.ResolveSlot(votes);
        Assert.Null(resolution.Agreed);
        Assert.Equal(1, resolution.Votes);
    }

    [Fact]
    public void Crafting_survives_a_ready_vote_and_agrees_with_a_later_crafting_vote()
    {
        var ready = new SlotVote(new FacilityObservation(FacilityPhase.ReadyToCollect, null, "M2肌肉注射剂", "M2肌肉注射剂"),
            800, "M2肌肉注射剂", "M2肌肉注射剂");
        var resolution = CollectFlow.ResolveSlot([Crafting(58, 9, 0), ready, Crafting(58, 7, 1600)]);
        Assert.NotNull(resolution.Agreed);
        Assert.Equal(FacilityPhase.Crafting, resolution.Agreed!.Phase);
        Assert.Equal(new TimeSpan(0, 58, 7), resolution.Agreed.Remaining);
    }

    [Fact]
    public void Two_ready_votes_agree_without_a_countdown()
    {
        var readyA = new SlotVote(new FacilityObservation(FacilityPhase.ReadyToCollect, null, "轻语战术消音器", "轻语战术消音器"),
            0, "轻语战术消音器", "轻语战术消音器");
        var readyB = readyA with { CapturedAtMs = 800 };
        var resolution = CollectFlow.ResolveSlot([readyA, readyB]);
        Assert.NotNull(resolution.Agreed);
        Assert.Equal(FacilityPhase.ReadyToCollect, resolution.Agreed!.Phase);
        Assert.Equal("轻语战术消音器", resolution.Agreed.ItemName);
    }

    [Fact]
    public void Batch_message_distinguishes_time_like_failure_from_empty_slots()
    {
        var batch = new ObservationBatch(
            new Dictionary<FacilityKey, FacilityObservation>(),
            new Dictionary<FacilityKey, SlotUnresolved>
            {
                [FacilityKey.PharmacyLab] = new(SlotUnresolvedReason.TimeLikeUnparsed, 0, "0J58：09\nM2肌肉注射剂"),
            }, "C:\\shots\\x.png", "");
        string message = batch.Message;
        Assert.Contains("读到时间形文本但无法解析", message);
        Assert.Contains("制药台", message);
        Assert.DoesNotContain("连续读空", message);
        Assert.False(batch.IsComplete);
    }

    private static SlotVote Unknown(string raw, long capturedAt) =>
        new(new FacilityObservation(FacilityPhase.Unknown, null, "", raw), capturedAt, "", raw);

    private static SlotVote Crafting(int minutes, int seconds, long capturedAt)
    {
        var remaining = new TimeSpan(0, minutes, seconds);
        return new SlotVote(new FacilityObservation(FacilityPhase.Crafting, remaining, "M2肌肉注射剂",
            "M2肌肉注射剂"), capturedAt, "M2肌肉注射剂", "M2肌肉注射剂");
    }
}
