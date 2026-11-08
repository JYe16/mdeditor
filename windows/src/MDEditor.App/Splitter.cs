using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MDEditor;

/// <summary>Draggable divider between the panes; also operable with the arrow keys.</summary>
internal sealed class Splitter : Grid
{
    public event Action<double>? Dragged;
    private bool _dragging;
    private double _pressOffset;

    public Splitter()
    {
        Width = 7;
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, L.T("Divider"));
        Children.Add(new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });

        PointerPressed += (_, e) =>
        {
            _dragging = CapturePointer(e.Pointer);
            _pressOffset = e.GetCurrentPoint(this).Position.X;
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_dragging) Dragged?.Invoke(e.GetCurrentPoint(this).Position.X - _pressOffset);
        };
        PointerReleased += (_, e) =>
        {
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
        };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            Dragged?.Invoke(e.Key == VirtualKey.Left ? -24 : 24);
            e.Handled = true;
        }
    }
}
