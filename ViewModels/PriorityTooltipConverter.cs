using System;
using System.Globalization;
using Avalonia.Data.Converters;
using FluxMux.Avalonia.Services;

namespace FluxMux.Avalonia.ViewModels;

public sealed class PriorityTooltipConverter : IValueConverter
{
    public static PriorityTooltipConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var goal = LocalPrioritySettingsCalculator.CanonicalizeGoalName(value?.ToString());
        var cloud = string.Equals(parameter?.ToString(), "cloud", StringComparison.OrdinalIgnoreCase);
        return goal switch
        {
            "Stability" => cloud
                ? "Put this first for more conservative, repeatable answers. Put it lower if you will accept more varied wording. This does not change the token bill as much as Context length, Reasoning depth, or Reply length."
                : "Put this first for fewer crashes and out-of-memory stops. Put it lower if you will accept more VRAM risk for speed or a longer chat. This does not make answers smarter.",
            "Speed" => cloud
                ? "Put this first for quicker, cheaper turns (less reasoning, often a smaller window). Put it lower if you will wait and pay for more careful answers or a longer chat. Faster can mean thinner answers."
                : "Put this first for quicker answers. Put it lower if you will wait for more careful answers or a longer chat. Faster usually means a smaller context window and less VRAM, and can mean messier wording.",
            "Fidelity" => "Put this first so answers stick closer to what you asked (higher-precision chat memory, which uses more VRAM). Put it lower if you will trade some accuracy for speed or VRAM. This does not turn reasoning on, and it does not set how long one answer can be.",
            "Context length" => cloud
                ? "Put this first so more of the chat stays in the cloud model's mind. A longer window sends more history each turn, so token cost goes up. Put it lower to save cost and time. Shorter memory means earlier details are forgotten. This is not how long one answer can be."
                : "Put this first so more of the chat stays in mind. Put it lower to free VRAM and go faster. Shorter memory means earlier details are forgotten as the chat grows. Changing this later reloads the model. This is not how long one answer can be.",
            "Reply length" => cloud
                ? "Put this first so one answer can be a long dump of code or text. Longer answers cost more tokens. Put it lower for quicker, cheaper, shorter turns. A short limit can cut an answer off mid-sentence. This is not how much of the past chat is remembered."
                : "Put this first so one answer can be a long dump of code or text. Put it lower for quicker, shorter turns. A short limit can cut an answer off mid-sentence. This is not how much of the past chat is remembered, and it does not by itself use extra VRAM.",
            "Reasoning depth" => cloud
                ? "Put this first so the cloud model thinks longer before it writes (when the company supports it). That uses extra tokens and usually raises the bill. Put it lower for faster, cheaper turns. Higher is slower; it often helps hard problems more than coding."
                : "Put this first for a hidden thinking step before the answer. For writing code, Off is usually better (faster, cleaner). Hidden thinking fills the context window, so it uses VRAM and eats the reply budget. Changing this later reloads the model.",
            "Token Cost Economy" => "Put this first to spend fewer cloud credits. Put it lower if you will pay for fuller thinking or a longer written answer. Cheaper can mean thinner answers.",
            _ => "Use the up and down buttons to rank this goal. Top of the list matters most when AI-FluxMux fills in settings."
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
