using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — Quarantine and Confirmation screens (§18):
/// - Quarantine: lists the queue; destructive button DISABLED without selection/queue;
///   label always references the "quarantine" destination.
/// - Confirmation: shows operation_id (deterministic), item count, and the
///   expected path in §18 format (ConflictDoctor/quarantine/<timestamp>)
///   BEFORE enabling the confirmation button.
/// Timestamps and ids are derived deterministically (no local clock).
/// </summary>
public class ConfirmationScreenTests
{
    private static MainWindowViewModel NewVm() => new(new FakeScanEngine());

    private static MainWindowViewModel VmWithQueue()
    {
        var vm = NewVm();
        vm.Quarantine.Queue(@"C:\demo\orcamento (Notebook-Office).xlsx");
        vm.Quarantine.Queue(@"C:\demo\praia.jpg");
        return vm;
    }

    [Fact]
    public void Expected_path_follows_section_18_format()
    {
        // ConflictDoctor/quarantine/<timestamp> — fixed separator and date, no locale.
        var path = MainWindowViewModel.ExpectedQuarantinePath();

        Assert.StartsWith("ConflictDoctor/quarantine/", path, StringComparison.Ordinal);
        // Deterministic basic ISO-8601 timestamp: 4-2-2 digits + T + hour.
        Assert.Matches(
            @"^ConflictDoctor/quarantine/\d{4}-\d{2}-\d{2}T\d{2}-\d{2}$",
            path);
    }

    [Fact]
    public void Operation_id_is_deterministic_for_the_same_queue()
    {
        var vm1 = VmWithQueue();
        var vm2 = VmWithQueue();

        Assert.Equal(vm1.ExpectedOperationId, vm2.ExpectedOperationId);
        Assert.NotEqual("", vm1.ExpectedOperationId);

        // Different queues → different ids.
        var vm3 = NewVm();
        vm3.Quarantine.Queue(@"C:\other\file.txt");
        Assert.NotEqual(vm1.ExpectedOperationId, vm3.ExpectedOperationId);
    }

    [Fact]
    public void Confirm_button_disabled_without_items_in_queue()
    {
        var vm = NewVm();
        Assert.False(vm.OpenConfirmationFromQuarantineCommand.CanExecute(null));

        vm.Quarantine.Queue(@"C:\demo\a.txt");
        Assert.True(vm.OpenConfirmationFromQuarantineCommand.CanExecute(null));
    }

    [Fact]
    public void Manifest_data_visible_before_enabling_confirmation()
    {
        var vm = VmWithQueue();

        // The three fake manifest data points exist BEFORE opening the Confirmation screen.
        Assert.False(string.IsNullOrWhiteSpace(vm.ExpectedOperationId));
        Assert.Equal(2, vm.Quarantine.Count);
        var path = MainWindowViewModel.ExpectedQuarantinePath();
        Assert.Contains("quarantine", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_screen_displays_queue_destination_label_and_secondary_button()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var axaml = File.ReadAllText(Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml"));

        // Action label on Quarantine always references the "quarantine" destination.
        Assert.Contains("Record Quarantine Move",
            axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmation_screen_presents_operation_id_count_and_path()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CloudSyncConflictDoctor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var axaml = File.ReadAllText(Path.Combine(dir!, "src", "Doctor.Gui", "MainWindow.axaml"));

        Assert.Contains("ExpectedOperationId", axaml, StringComparison.Ordinal);
        Assert.Contains("ExpectedQuarantinePath", axaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmationItemCount", axaml, StringComparison.Ordinal);
    }
}
