using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class DisplayAdapterLabelingTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090 — OK", "NVIDIA GeForce RTX 4090", "OK", "main graphics card", "dedicated VRAM")]
    [InlineData("Intel(R) UHD Graphics 630 — OK", "Intel(R) UHD Graphics 630", "OK", "motherboard graphics chip", "shared system RAM")]
    [InlineData("Intel(R) Arc A770 Graphics — OK", "Intel(R) Arc A770 Graphics", "OK", "main graphics card", "dedicated VRAM")]
    [InlineData("Microsoft Basic Display Adapter — OK", "Microsoft Basic Display Adapter", "OK", null, "no dedicated VRAM")]
    public void ParseRow_splits_adapter_status_and_memory_notes(
        string line,
        string expectedName,
        string expectedStatus,
        string? roleFragment,
        string memoryFragment)
    {
        var row = DisplayAdapterLabeling.ParseRow(line);

        Assert.Equal(expectedName, row.NameLabel);
        Assert.Equal(expectedStatus, row.StatusLabel);
        if (roleFragment is null)
        {
            Assert.DoesNotContain("main graphics card", row.NotesLabel);
            Assert.DoesNotContain("motherboard graphics chip", row.NotesLabel);
        }
        else
        {
            Assert.Contains(roleFragment, row.NotesLabel);
        }

        Assert.Contains(memoryFragment, row.NotesLabel);
    }

    [Fact]
    public void FormatNotesColumn_puts_role_and_memory_on_one_line_with_colon()
    {
        var notes = DisplayAdapterLabeling.FormatNotesColumn("NVIDIA GeForce RTX 4090");

        Assert.Equal("main graphics card: dedicated VRAM — local models can use this", notes);
        Assert.DoesNotContain('\n', notes);
    }

    [Fact]
    public void ClassifyMemoryKind_marks_integrated_amd_as_shared_system_ram()
    {
        Assert.Equal(
            DisplayAdapterLabeling.AdapterMemoryKind.SharedSystemRam,
            DisplayAdapterLabeling.ClassifyMemoryKind("AMD Radeon(TM) Graphics"));
    }
}
