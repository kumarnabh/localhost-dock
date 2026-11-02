using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using LocalhostDock.Config;
using LocalhostDock.Core;
using LocalhostDock.UI;
using Xunit;

namespace LocalhostDock.Tests;

/// <summary>
/// Builds each dialog off-screen (never where a user could click it), lays it out, renders it to a PNG and checks
/// that every visible control fits inside the window. Renders go to %TEMP%\ldock-ui-smoke for manual review.
/// </summary>
public class UiSmokeTests
{
    public static readonly string OutDir = Path.Combine(Path.GetTempPath(), "ldock-ui-smoke");

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static void Render(Form form, string name)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000); // off-screen: nothing to click
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        Directory.CreateDirectory(OutDir);
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(OutDir, name + ".png"), ImageFormat.Png);
        }
        AssertFits(form, form);
        form.Close();
        form.Dispose();
    }

    /// <summary>Every visible control's bounds lie inside its parent's client area (catches collapsed/overflowing layouts).</summary>
    private static void AssertFits(Control root, Form form)
    {
        foreach (Control c in root.Controls)
        {
            if (!c.Visible) continue;
            if (c is Label { Text.Length: 0 }) continue; // empty grid spacers are meant to be zero-width
            if (c is Panel && c.Controls.Count > 0 && !c.Controls.Cast<Control>().Any(x => x.Visible)) continue; // holder of a hidden control
            var client = root.ClientRectangle;
            Assert.True(c.Width > 0 && c.Height > 0, $"{form.Text}: {c.GetType().Name} “{c.Text}” collapsed to {c.Size}");
            if (root is not ScrollableControl { AutoScroll: true })
                Assert.True(c.Right <= client.Right + 2 && c.Bottom <= client.Bottom + 2,
                    $"{form.Text}: {c.GetType().Name} “{c.Text}” at {c.Bounds} overflows {root.GetType().Name} {client}");
            AssertFits(c, form);
        }
    }

    private static Registry SampleRegistry() => new()
    {
        Apps =
        {
            new AppEntry { Id = "api", Name = "Shop API", Port = 8000, Health = "/healthz" },
            new AppEntry { Id = "web", Name = "Shop Web", Port = 3001 },
            new AppEntry { Id = "wrk", Name = "Shop Worker", Port = 8010 },
            new AppEntry { Id = "doc", Name = "Docs site", Port = 4000, Start = "npm run docs -- --port 4000" },
        },
        Workspaces = { new WorkspaceEntry { Id = "shop", Name = "Shop dev", Apps = { "api", "web", "wrk" }, DependsOn = { ["web"] = new() { "api" }, ["wrk"] = new() { "api" } } } },
    };

    [Fact]
    public void Preset_import_review_renders_and_fits() => OnSta(() => Render(new PresetImportDialog(
        Workspace.WorkspacePresets.Read(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../presets/nabh-projects.example.json")))), "preset-import"));

    [Fact]
    public void Workspace_navigation_filters_members_and_search_without_starting() => OnSta(() =>
    {
        using var controller = new AppController(SampleRegistry());
        using var dock = new DockForm(controller, () => { });
        var snapshot = new Snapshot(controller.Registry.Apps.Select(a => new AppStatus(a, AppState.Down,
            Array.Empty<OwnedListener>(), null, 0, null, null)).ToList(),
            new[] { new WorkspaceStatus(controller.Registry.Workspaces[0], Array.Empty<AppStatus>()) },
            Array.Empty<DiscoveredPort>(), Array.Empty<LocalhostDock.Net.Listener>());
        dock.Render(snapshot);
        dock.NavigateWorkspace("shop");
        // Inspect visibility from an off-screen window; no service actions are invoked by navigation.
        dock.StartPosition = FormStartPosition.Manual;
        dock.Location = new Point(-32000, -32000);
        dock.Show();
        var list = dock.Controls.OfType<FlowLayoutPanel>().Single();
        Assert.Equal(3, list.Controls.OfType<AppRow>().Count(r => r.Visible));
        dock.NavigateWorkspace(null);
        Assert.Equal(4, list.Controls.OfType<AppRow>().Count(r => r.Visible));
        dock.Close();
    });

    [Fact]
    public void Workspace_editor_renders_and_fits() => OnSta(() =>
    {
        var reg = SampleRegistry();
        Render(new WorkspaceEditor(reg.Workspaces[0].Clone(), reg, "Edit Shop dev"), "workspace-editor");
    });

    [Fact]
    public void App_editor_with_health_fields_renders_and_fits() => OnSta(() =>
    {
        var reg = SampleRegistry();
        Render(new AppEditor(reg.Apps[0].Clone(), reg, "Edit Shop API"), "app-editor");
    });

    [Fact]
    public void Conflict_wizard_renders_and_fits() => OnSta(() =>
    {
        using var tp = new TestProcesses();
        int port = TestProcesses.FreePort();
        tp.Listen(port, tp.TempDir("holder"));
        var reg = SampleRegistry();
        reg.Apps[3].Port = port;
        reg.Apps[3].Start = $"npm run docs -- --port {port}";
        reg.Apps[3].Cwd = tp.TempDir("docs");
        using var controller = new AppController(reg);
        var wizard = new ConflictWizard(reg.Apps[3], port, reg, controller);
        Assert.NotNull(wizard.Holder);
        Assert.Contains(":", wizard.Text);
        Render(wizard, "conflict-wizard");
    });

    [Fact]
    public void Restore_dialog_renders_and_fits() => OnSta(() =>
    {
        var reg = SampleRegistry();
        Render(new RestoreDialog(reg.Apps.Take(3).ToList(), DateTime.UtcNow.AddHours(-3)), "restore-dialog");
    });
}
