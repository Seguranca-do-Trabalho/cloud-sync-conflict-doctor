using Doctor.Gui.Engine;
using Doctor.Gui.ViewModels;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// T06 — ciclo 3 (RED): segurança da fila de quarentena e determinismo da sugestão.
/// ADR-0002: única operação permitida sobre conteúdo do usuário é mover para quarentena;
/// nunca "apagar". ADR-0003: empate resolvido mtime → size → path, caminho em bytes UTF-8.
/// </summary>
public class SegurancaTests
{
    private static MainWindowViewModel NovoVm() => new(new FakeScanEngine());

    [Fact]
    public void Fila_de_quarentena_nao_duplica_item_repetido()
    {
        var vm = NovoVm();
        vm.QueueForQuarantineInternal("C:/demo/a.txt");
        vm.QueueForQuarantineInternal("C:/demo/a.txt");

        Assert.Equal(["C:/demo/a.txt"], vm.QuarantineQueue.ToArray());
    }

    [Fact]
    public void Sugestao_de_versao_a_manter_usa_mtime_depois_size_depois_path_em_bytes()
    {
        // mtime vence; empatando, maior size vence; empatando, menor caminho em bytes UTF-8.
        var grupo = new ConflictGroup
        {
            BaseName = "doc.txt",
            Versions =
            [
                new ConflictVersion { Path = "doc (b).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (a).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (grande).txt", SizeBytes = 999,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (novo).txt", SizeBytes = 5,
                    MtimeUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) },
            ],
        };

        var mantida = MainWindowViewModel.SuggestVersionToKeep(grupo);

        Assert.Equal("doc (novo).txt", mantida!.Path); // mtime mais recente
    }

    [Fact]
    public void Empate_total_de_mtime_e_size_prefere_menor_caminho_em_bytes_utf8()
    {
        var grupo = new ConflictGroup
        {
            BaseName = "doc.txt",
            Versions =
            [
                new ConflictVersion { Path = "doc (B).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ConflictVersion { Path = "doc (A).txt", SizeBytes = 10,
                    MtimeUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            ],
        };

        var mantida = MainWindowViewModel.SuggestVersionToKeep(grupo);

        Assert.Equal("doc (A).txt", mantida!.Path);
    }

    [Fact]
    public void Confirmacao_da_quarentena_exige_fila_nao_vazia()
    {
        var vm = NovoVm();
        Assert.False(vm.ConfirmQuarantineCommand.CanExecute(null)); // vazia: desabilitado

        vm.QueueForQuarantineInternal("C:/demo/a.txt");
        Assert.True(vm.ConfirmQuarantineCommand.CanExecute(null));
    }

    [Fact]
    public void Comparar_enfileira_apenas_as_versoes_que_nao_sao_mantidas()
    {
        var vm = NovoVm();
        vm.ChosenFolder = @"C:\demo";
        vm.StartScanCommand.Execute(null);

        var grupo = vm.Report!.RealConflicts[0];
        vm.CompareConflictCommand.Execute(grupo);
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);

        // Mantida = versão com mtime mais recente (13/08); as outras 2 entram na fila.
        Assert.Equal(2, vm.QuarantineCount);
        Assert.DoesNotContain(vm.SelectedVersionToKeep!.Path, vm.QuarantineQueue);
        Assert.All(vm.QuarantineQueue, p => Assert.NotEqual(vm.SelectedVersionToKeep!.Path, p));

        // Repetir a operação não duplica itens na fila.
        vm.QueueOtherVersionsForQuarantineCommand.Execute(null);
        Assert.Equal(2, vm.QuarantineCount);
    }
}
