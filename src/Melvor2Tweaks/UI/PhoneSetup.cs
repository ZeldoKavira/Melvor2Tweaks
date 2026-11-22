using System;
using Godot;

namespace Melvor2Tweaks.UI;

/// <summary>
/// The "Phone access" block on the Melvor2Tweaks settings page, under its switches: status, the address the
/// phone uses, a QR code of the login link (hidden until asked for, since it contains the key), Copy link,
/// New key, and the reverse-proxy addresses allowed to connect.
/// </summary>
internal static class PhoneSetup
{
    private static readonly Color Gold = new(0.98f, 0.8f, 0.3f);
    private static readonly Color Muted = new(0.75f, 0.71f, 0.62f);
    private static readonly Color Green = new(0.55f, 0.88f, 0.55f);
    private static readonly Color Red = new(1f, 0.5f, 0.42f);

    internal static Control Build(LabelSettings? descStyle)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.25f), BorderColor = new Color(0.55f, 0.45f, 0.25f, 0.6f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 14, ContentMarginBottom = 14,
        });
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 24);
        card.AddChild(cols);

        // Left: status, address, buttons, proxy addresses.
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 10);
        cols.AddChild(left);

        var status = TopBar.RawLabel("", 16, Green);
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        left.AddChild(status);

        left.AddChild(Caption("Address your phone opens", descStyle));
        var url = new LineEdit
        {
            PlaceholderText = "https://melvor.example.com  (leave empty to use this PC only)",
            Text = Settings.Current.RemotePublicUrl ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 38),
        };
        left.AddChild(url);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var showQr = TopBar.GameButton("Show QR code");
        var copy = TopBar.GameButton("Copy link");
        var newKey = TopBar.GameButton("New key");
        foreach (var b in new[] { showQr, copy, newKey }) { b.CustomMinimumSize = new Vector2(140, 38); buttons.AddChild(b); }
        left.AddChild(buttons);
        UiKit.AddTooltip(copy, "Copy link", "The login link (address + secret key). Anyone with it can control this game: only send it to yourself.");
        UiKit.AddTooltip(newKey, "New key", "Logs every phone out. Use it if the link or QR code got out.");

        var note = TopBar.RawLabel("", 14, Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        left.AddChild(note);

        left.AddChild(Caption("Reverse proxy allowed to connect (with \"Let your server connect\" on)", descStyle));
        var allowed = new LineEdit
        {
            PlaceholderText = "Its address, e.g. 192.168.1.10 (comma separated for several)",
            Text = Settings.Current.RemoteAllowedClients ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 38),
        };
        left.AddChild(allowed);

        left.AddChild(Caption("Scan the QR code with your phone's camera (or open the copied link) once; the phone stays logged in. " +
                              "To use it away from this PC, point a reverse proxy or tunnel at this PC's port and turn on \"Let your server connect\".", descStyle));

        // Right: the QR code, hidden until asked for (it holds the key).
        var qrBox = new VBoxContainer { Visible = false };
        qrBox.AddThemeConstantOverride("separation", 6);
        var qr = new TextureRect
        {
            CustomMinimumSize = new Vector2(220, 220),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        };
        qrBox.AddChild(qr);
        var qrCaption = TopBar.RawLabel("Scan with your phone", 14, Muted);
        qrCaption.HorizontalAlignment = HorizontalAlignment.Center;
        qrBox.AddChild(qrCaption);
        cols.AddChild(qrBox);

        void SaveFields()
        {
            Settings.Current.RemotePublicUrl = url.Text.Trim();
            Settings.Current.RemoteAllowedClients = allowed.Text.Trim();
            Settings.Save();
        }

        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(status)) return;
            var s = Settings.Current;
            bool on = s.RemoteEnabled, running = Remote.RemoteServer.Running;
            status.Text = !on ? "○  Phone access is off - turn it on above"
                : !running ? $"●  Couldn't start: {Remote.RemoteServer.LastError}"
                : $"●  Running on port {Remote.RemoteServer.Port}" + (s.RemoteLan ? $", accepting {(string.IsNullOrWhiteSpace(s.RemoteAllowedClients) ? "no other address yet" : s.RemoteAllowedClients)}" : ", this PC only");
            status.AddThemeColorOverride("font_color", !on ? Muted : running ? Green : Red);
            foreach (var b in new[] { showQr, copy, newKey }) b.Disabled = !on;
            if (qrBox.Visible) DrawQr();
        }

        void DrawQr()
        {
            try { qr.Texture = QrCode.Encode(Remote.RemoteServer.LoginLink()).ToTexture(6); }
            catch (Exception e) { note.Text = "Couldn't make the QR code: " + e.Message; }
        }

        url.TextSubmitted += _ => { SaveFields(); Refresh(); };
        url.FocusExited += () => { SaveFields(); Refresh(); };
        allowed.TextSubmitted += _ => { SaveFields(); Refresh(); };
        allowed.FocusExited += () => { SaveFields(); Refresh(); };
        showQr.Pressed += () =>
        {
            SaveFields();
            qrBox.Visible = !qrBox.Visible;
            showQr.Text = qrBox.Visible ? "Hide QR code" : "Show QR code";
            if (qrBox.Visible) DrawQr();
        };
        copy.Pressed += () =>
        {
            SaveFields();
            DisplayServer.ClipboardSet(Remote.RemoteServer.LoginLink());
            note.Text = "Link copied. Open it on your phone once; it stays logged in.";
        };
        newKey.Pressed += () =>
        {
            Remote.RemoteServer.ResetKey();
            note.Text = "New key made: phones are logged out until they scan the new code or open the new link.";
            Refresh();
        };
        // Hide the QR again when the page is left (it holds the key).
        card.VisibilityChanged += () => { if (!card.IsVisibleInTree() && qrBox.Visible) { qrBox.Visible = false; showQr.Text = "Show QR code"; } };

        Action onChanged = () => Callable.From(Refresh).CallDeferred();
        Settings.Changed += onChanged;
        card.TreeExiting += () => Settings.Changed -= onChanged;
        Refresh();
        return card;
    }

    private static Label Caption(string text, LabelSettings? style) => new()
    {
        Text = text,
        LabelSettings = style,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
}
