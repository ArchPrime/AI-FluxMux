using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace FluxMux.Avalonia.Views;

/// <summary>
/// Opens the Help tab at a topic's stable <c>topic.*</c> id. The Heading 2 title
/// can change when Help.html is updated; this id must stay.
/// </summary>
public sealed class HelpTopicLink : Button
{
    public static readonly StyledProperty<string> TopicIdProperty =
        AvaloniaProperty.Register<HelpTopicLink, string>(nameof(TopicId), string.Empty);

    public HelpTopicLink()
    {
        Classes.Add("helpTopicLink");
        Content ??= "Help";
        Click += (_, _) =>
        {
            var id = TopicId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            this.FindAncestorOfType<MainWindow>()?.ShowHelpTopic(id);
        };
    }

    public string TopicId
    {
        get => GetValue(TopicIdProperty);
        set => SetValue(TopicIdProperty, value);
    }
}
