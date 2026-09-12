// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

namespace Prowl.Tweening.Tests;

/// <summary>
/// Easing: the equations themselves, and the two places the defaults used to be resolved
/// inconsistently.
/// </summary>
public sealed class EasingTests : TweenTestBase
{
    [Theory]
    [InlineData(Ease.Linear)]
    [InlineData(Ease.InSine)]
    [InlineData(Ease.OutSine)]
    [InlineData(Ease.InOutSine)]
    [InlineData(Ease.InQuad)]
    [InlineData(Ease.OutQuad)]
    [InlineData(Ease.InOutQuad)]
    [InlineData(Ease.InCubic)]
    [InlineData(Ease.OutCubic)]
    [InlineData(Ease.InOutCubic)]
    [InlineData(Ease.InQuart)]
    [InlineData(Ease.OutQuart)]
    [InlineData(Ease.InOutQuart)]
    [InlineData(Ease.InQuint)]
    [InlineData(Ease.OutQuint)]
    [InlineData(Ease.InOutQuint)]
    [InlineData(Ease.InExpo)]
    [InlineData(Ease.OutExpo)]
    [InlineData(Ease.InOutExpo)]
    [InlineData(Ease.InCirc)]
    [InlineData(Ease.OutCirc)]
    [InlineData(Ease.InOutCirc)]
    [InlineData(Ease.InElastic)]
    [InlineData(Ease.OutElastic)]
    [InlineData(Ease.InOutElastic)]
    [InlineData(Ease.InBack)]
    [InlineData(Ease.OutBack)]
    [InlineData(Ease.InOutBack)]
    [InlineData(Ease.InBounce)]
    [InlineData(Ease.OutBounce)]
    [InlineData(Ease.InOutBounce)]
    public void EveryEaseStartsAtZeroEndsAtOneAndStaysFinite(Ease ease)
    {
        Assert.Equal(0f, EaseUtility.Evaluate(ease, 0f), 4);
        Assert.Equal(1f, EaseUtility.Evaluate(ease, 1f), 4);

        for (int i = 0; i <= 100; i++)
        {
            float value = EaseUtility.Evaluate(ease, i / 100f);
            Assert.True(float.IsFinite(value), $"{ease} produced {value} at t={i / 100f}");
        }
    }

    [Fact]
    public void UnsetResolvesToTheConfiguredDefaultRatherThanFallingBackToLinear()
    {
        Tween.DefaultEase = Ease.InQuad;

        var box = new Box();
        Tween t = Tween.To(0f, 10f, 1f).Bind(box, SetValue).SetEase(Ease.Unset);

        Tween.Update(0.5f);

        // InQuad at the half-way point is 0.25, not the 0.5 a silent linear fallback would give.
        Assert.Equal(2.5f, box.Value, 3);
        Assert.True(t.IsActive);
    }

    [Fact]
    public void ANewTweenPicksUpTheConfiguredDefault()
    {
        Tween.DefaultEase = Ease.Linear;

        var box = new Box();
        Tween.To(0f, 10f, 1f).Bind(box, SetValue);

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void TheDefaultEaseRejectsValuesThatCannotBeResolved()
    {
        Tween.DefaultEase = Ease.OutBack;

        Tween.DefaultEase = Ease.Unset;
        Assert.Equal(Ease.Linear, Tween.DefaultEase);

        Tween.DefaultEase = Ease.Custom;
        Assert.Equal(Ease.Linear, Tween.DefaultEase);
    }

    [Fact]
    public void EasedValueUsesTheSameDefaultsATweenWould()
    {
        Tween.DefaultEaseOvershootOrAmplitude = 1.70158f;
        float standard = Tween.EasedValue(0f, 1f, 0.5f, Ease.OutBack);

        Tween.DefaultEaseOvershootOrAmplitude = 6f;
        float exaggerated = Tween.EasedValue(0f, 1f, 0.5f, Ease.OutBack);

        // Sampling used to read a private constant instead of the global, so changing the global
        // had no effect here at all.
        Assert.NotEqual(standard, exaggerated);
    }

    [Fact]
    public void EasedValueAcceptsUnsetToo()
    {
        Tween.DefaultEase = Ease.Linear;
        Assert.Equal(5f, Tween.EasedValue(0f, 10f, 0.5f, Ease.Unset), 3);
    }

    [Fact]
    public void OvershootAndPeriodRejectNonsense()
    {
        Tween.DefaultEaseOvershootOrAmplitude = 2f;
        Tween.DefaultEaseOvershootOrAmplitude = float.NaN;
        Assert.Equal(2f, Tween.DefaultEaseOvershootOrAmplitude);

        Tween.DefaultEasePeriod = 0.3f;
        Tween.DefaultEasePeriod = -1f;
        Assert.Equal(0.3f, Tween.DefaultEasePeriod);
    }

    [Fact]
    public void ACustomEaseDrivesTheTween()
    {
        var box = new Box();

        // A step function, so the result is unmistakably not one of the built-in curves.
        Tween.To(0f, 10f, 1f)
            .Bind(box, SetValue)
            .SetEase(static (time, duration, overshoot, period) => time < 0.5f ? 0f : 1f);

        Tween.Update(0.25f);
        Assert.Equal(0f, box.Value, 3);

        Tween.Update(0.5f);
        Assert.Equal(10f, box.Value, 3);
    }

    [Fact]
    public void SettingAStandardEaseClearsAPreviousCustomOne()
    {
        var box = new Box();
        Tween t = Tween.To(0f, 10f, 1f)
            .Bind(box, SetValue)
            .SetEase(static (time, duration, overshoot, period) => 1f)
            .SetEase(Ease.Linear);

        Assert.False(FlagsOf(t).HasFlag(TweenFlags.CustomEase));

        Tween.Update(0.5f);
        Assert.Equal(5f, box.Value, 3);
    }

    [Fact]
    public void BackAndElasticHonourTheirParameters()
    {
        float gentle = EaseUtility.Evaluate(Ease.OutBack, 0.5f, 1f);
        float wild = EaseUtility.Evaluate(Ease.OutBack, 0.5f, 8f);

        Assert.NotEqual(gentle, wild);
        Assert.True(wild > gentle, "a larger overshoot should push further past the target");
    }
}
