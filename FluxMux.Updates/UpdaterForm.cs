using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FluxMux.Updates;

/// <summary>
/// Progress dialog shown while the standalone updater runs. Displays the current step,
/// a progress bar (determinate when byte counts are known, marquee otherwise), and a
/// final "Restart AI-FluxMux" / "Close" button once the update finishes.
/// </summary>
public sealed class UpdaterForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Label _progressLabel;
    private readonly Button _actionButton;
    private readonly Label _detailLabel;

    private string _action = "Close";
    private Action? _actionCallback;
    private bool _finished;

    /// <summary>True once <see cref="Finish"/> has been called (the form is no longer in progress).</summary>
    public bool IsFinished => _finished;

    public UpdaterForm(string title)
    {
        // DPI-aware: scale all controls with the system font/DPI.
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Text = "AI-FluxMux Updater";

        _titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _statusLabel = new Label
        {
            Text = "Starting...",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _progressBar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Height = 18,
            // Width is set below from a DPI-aware measurement.
            Margin = new Padding(0, 0, 0, 4)
        };

        _progressLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2)
        };

        _detailLabel = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            // MaximumSize is set below from a DPI-aware measurement.
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 0)
        };

        _actionButton = new Button
        {
            Text = "Close",
            Enabled = false,
            AutoSize = true,
            MinimumSize = new Size(160, 32)
        };
        _actionButton.Click += OnActionButtonClick;

        // DPI-aware content width: derive it from the title font so the progress bar
        // and detail label scale with the system DPI/font instead of using a static
        // pixel value. ~28 typical characters at the title size gives a comfortable
        // width that grows on high-DPI / large-font displays.
        var titleFont = new Font("Segoe UI", 12F, FontStyle.Bold);
        var contentWidth = Math.Max(
            320,
            TextRenderer.MeasureText("Updating llama-server", titleFont).Width + 80);
        titleFont.Dispose();

        _progressBar.Width = contentWidth;
        _detailLabel.MaximumSize = new Size(contentWidth, 0);

        // Stack content top-to-bottom. A FlowLayoutPanel (TopDown) sizes itself to
        // its widest child and stacks the rest, which avoids the width-collapse
        // problems a Dock=Fill TableLayoutPanel has inside a GrowAndShrink form.
        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 16, 16, 4)
        };
        content.Controls.Add(_titleLabel);
        content.Controls.Add(_statusLabel);
        content.Controls.Add(_progressBar);
        content.Controls.Add(_progressLabel);
        content.Controls.Add(_detailLabel);

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 8, 16, 12)
        };
        _actionButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        buttonPanel.Controls.Add(_actionButton);

        Controls.Add(buttonPanel);
        Controls.Add(content);

        // Let the form size to its content once the controls have measured.
        AcceptButton = _actionButton;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    private void OnActionButtonClick(object? sender, EventArgs e)
    {
        var callback = _actionCallback;
        _actionCallback = null;
        _actionButton.Enabled = false;
        callback?.Invoke();
    }

    /// <summary>Updates the status text.</summary>
    public void SetStatus(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(message)); return; }
        _statusLabel.Text = message;
    }

    /// <summary>Reports byte-level download progress. totalBytes < 0 means unknown total.</summary>
    public void SetProgress(long bytesDone, long totalBytes)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetProgress(bytesDone, totalBytes)); return; }

        if (totalBytes > 0)
        {
            _progressBar.Style = ProgressBarStyle.Continuous;
            var percent = (int)Math.Min(100, (bytesDone * 100) / totalBytes);
            _progressBar.Value = Math.Max(0, Math.Min(100, percent));
            _progressLabel.Text = $"{FormatBytes(bytesDone)} / {FormatBytes(totalBytes)}  ({percent}%)";
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressLabel.Text = FormatBytes(bytesDone) + " downloaded";
        }
    }

    /// <summary>Shows a detail line (e.g. the backup path) under the progress bar.</summary>
    public void SetDetail(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetDetail(text)); return; }
        _detailLabel.Text = text;
    }

    /// <summary>
    /// Marks the update as finished and enables the action button.
    /// On success the button reads "Restart AI-FluxMux" (or "Close" if the app exe
    /// isn't found); on failure it reads "Close".
    /// </summary>
    public void Finish(bool succeeded, string summary, string? detail)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Finish(succeeded, summary, detail)); return; }
        if (_finished) return;
        _finished = true;

        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Value = 100;
        _statusLabel.Text = summary;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            _detailLabel.Text = detail;
        }

        if (succeeded)
        {
            var appExe = Path.Combine(AppContext.BaseDirectory, "FluxMux.Avalonia.exe");
            if (File.Exists(appExe))
            {
                _action = "Restart AI-FluxMux";
                _actionCallback = () =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = appExe,
                            WorkingDirectory = AppContext.BaseDirectory,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // If launch fails the form just closes; the user can start manually.
                    }
                    Close();
                };

                // Make the restart button the clear primary action: bigger, and the
                // form's default (Enter) button.
                _actionButton.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
                AcceptButton = _actionButton;
            }
            else
            {
                _action = "Close";
                _actionCallback = Close;
            }
        }
        else
        {
            _action = "Close";
            _actionCallback = Close;
        }

        _actionButton.Text = _action;
        _actionButton.Enabled = true;
        _actionButton.Focus();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
        return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
    }
}