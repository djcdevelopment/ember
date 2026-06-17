using Ember.Reflect;
using Xunit;

namespace Ember.Tests;

/// <summary>Pins the one-run-per-local-day semantics of the reflect scheduler.</summary>
public class ReflectScheduleTests
{
    private static readonly TimeOnly RunAt = new(3, 0);

    [Fact]
    public void Not_due_before_the_run_time()
    {
        var now = new DateTime(2026, 6, 11, 2, 59, 0);
        Assert.False(ReflectService.IsDue(now, RunAt, latestRunDate: null));
    }

    [Fact]
    public void Due_after_the_run_time_with_no_prior_run()
    {
        var now = new DateTime(2026, 6, 11, 3, 0, 0);
        Assert.True(ReflectService.IsDue(now, RunAt, latestRunDate: null));
    }

    [Fact]
    public void Not_due_when_today_already_ran()
    {
        var now = new DateTime(2026, 6, 11, 9, 0, 0);
        Assert.False(ReflectService.IsDue(now, RunAt, latestRunDate: "2026-06-11"));
    }

    [Fact]
    public void Due_when_the_last_run_was_yesterday()
    {
        var now = new DateTime(2026, 6, 11, 3, 1, 0);
        Assert.True(ReflectService.IsDue(now, RunAt, latestRunDate: "2026-06-10"));
    }

    [Fact]
    public void A_missed_night_fires_on_late_boot_the_same_day()
    {
        // ember was down at 03:00 and starts at 14:30 — the run still happens today.
        var now = new DateTime(2026, 6, 11, 14, 30, 0);
        Assert.True(ReflectService.IsDue(now, RunAt, latestRunDate: "2026-06-10"));
    }

    [Fact]
    public void Schedule_is_disabled_in_manual_only_mode_or_with_a_blank_time()
    {
        // Manual-only (ADR 17): ScheduleEnabled=false, or a blank / whitespace / null run time.
        Assert.True(ReflectService.ScheduleDisabled(scheduleEnabled: false, "03:00"));
        Assert.True(ReflectService.ScheduleDisabled(scheduleEnabled: true, ""));
        Assert.True(ReflectService.ScheduleDisabled(scheduleEnabled: true, "   "));
        Assert.True(ReflectService.ScheduleDisabled(scheduleEnabled: true, null));
    }

    [Fact]
    public void Schedule_stays_on_when_enabled_with_a_run_time()
    {
        Assert.False(ReflectService.ScheduleDisabled(scheduleEnabled: true, "03:00"));
    }
}
