namespace VoiceFx;

/// <summary>
/// Scrolling panel with a solid background colour and see-through white behind
/// each section.
/// </summary>
sealed class BackgroundPanel : FlowLayoutPanel
{
    const int GlassAlpha = 215;

    public BackgroundPanel(Color color)
    {
        BackColor = color;
        DoubleBuffered = true;
    }

    // The colour a section ends up with: white glass over the background colour.
    Color SectionColor
    {
        get
        {
            int Blend(int c) => (c * (255 - GlassAlpha) + 255 * GlassAlpha) / 255;
            return Color.FromArgb(Blend(BackColor.R), Blend(BackColor.G), Blend(BackColor.B));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) => PaintScene(e.Graphics);

    /// <summary>
    /// Paints the background plus the white behind each section. Nested containers
    /// call this directly (via <see cref="PaintFor"/>) instead of relying on WinForms
    /// transparency, which only works reliably one level deep.
    /// </summary>
    void PaintScene(Graphics g)
    {
        g.Clear(BackColor);
        using var section = new SolidBrush(SectionColor);
        foreach (Control c in Controls)
            if (c is SectionBox && c.Visible)
                g.FillRectangle(section, c.Bounds);
    }

    /// <summary>Paints the background that lies under <paramref name="c"/>, in its coordinates.</summary>
    public static void PaintFor(Control c, PaintEventArgs e)
    {
        var root = c.Parent;
        while (root != null && root is not BackgroundPanel) root = root.Parent;
        if (root is not BackgroundPanel panel) return;

        var offset = panel.PointToClient(c.PointToScreen(Point.Empty));
        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform(-offset.X, -offset.Y);
        panel.PaintScene(e.Graphics);
        e.Graphics.Restore(state);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        MatchTrackBarColors(this);
    }

    // Track bars can't be transparent, so give them the section colour.
    void MatchTrackBarColors(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is TrackBar bar) bar.BackColor = SectionColor;
            MatchTrackBarColors(c);
        }
    }
}

// Containers that show the background (colour + section white) from the BackgroundPanel.
// Labels and checkboxes inside them can then simply use BackColor = Transparent.

/// <summary>A section with a title. If <see cref="Collapsible"/>, clicking the title folds it.</summary>
sealed class SectionBox : GroupBox
{
    string title = "";
    bool collapsed;

    public SectionBox() => BackColor = Color.Transparent;

    public event EventHandler? CollapsedChanged;

    public string Title
    {
        get => title;
        set { title = value; UpdateText(); }
    }

    public bool Collapsible { get; init; }

    public bool Collapsed
    {
        get => collapsed;
        set
        {
            if (!Collapsible || collapsed == value) return;
            collapsed = value;
            foreach (Control c in Controls) c.Visible = !value;
            UpdateText();
            CollapsedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void UpdateText() => Text = Collapsible ? (collapsed ? "▶  " : "▼  ") + title : title;

    bool OnTitle(Point p) => Collapsible && p.Y < Font.Height + 6;

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && OnTitle(e.Location)) Collapsed = !Collapsed;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = OnTitle(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => BackgroundPanel.PaintFor(this, e);
}

sealed class GlassTable : TableLayoutPanel
{
    public GlassTable() => BackColor = Color.Transparent;
    protected override void OnPaintBackground(PaintEventArgs e) => BackgroundPanel.PaintFor(this, e);
}

sealed class GlassFlow : FlowLayoutPanel
{
    public GlassFlow() => BackColor = Color.Transparent;
    protected override void OnPaintBackground(PaintEventArgs e) => BackgroundPanel.PaintFor(this, e);
}
