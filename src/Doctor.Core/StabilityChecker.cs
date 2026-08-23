namespace Doctor.Core;

/// <summary>
/// Verificador de estabilidade de arquivos (threat-model T-05, regra R4).
///
/// Protocolo:
/// 1. Capturar snapshot de metadados ANTES da leitura (size, mtime ticks, file_id)
/// 2. Ler conteúdo via IStreamSource
/// 3. Reler metadados DEPOIS da leitura
/// 4. Confrontar: divergência => UNSTABLE, stable => Stable
///
/// Fail-closed: qualquer divergência => dúvida sobre integridade => tratar como
/// potencialmente instável. Hash de arquivo instável JAMAIS é gravado no cache.
/// </summary>
public static class StabilityChecker
{
    /// <summary>
    /// Verifica se os metadados do arquivo permaneceram estáveis entre o snapshot
    /// pré-leitura e o estado pós-leitura.
    /// </summary>
    /// <param name="entry">Entry com metadados atuais (pós-leitura).</param>
    /// <param name="preSnapshot">Snapshot capturado antes da leitura.</param>
    /// <param name="postSnapshot">Snapshot capturado após a leitura.</param>
    /// <returns>Resultado da verificação de estabilidade.</returns>
    public static StabilityCheckResult Verificar(
        FileEntry entry,
        MetadataSnapshot preSnapshot,
        MetadataSnapshot postSnapshot)
    {
        // Verificação size
        if (postSnapshot.Size != preSnapshot.Size)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"size divergiu: pre={preSnapshot.Size}, post={postSnapshot.Size}");
        }

        // Verificação mtime (ticks)
        if (postSnapshot.MtimeTicks != preSnapshot.MtimeTicks)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"mtime divergiu: pre={preSnapshot.MtimeTicks}, post={postSnapshot.MtimeTicks}");
        }

        // Verificação file_id
        if (postSnapshot.FileId != preSnapshot.FileId)
        {
            return new StabilityCheckResult(
                FileStatus.Unstable,
                $"file_id divergiu: pre={preSnapshot.FileId}, post={postSnapshot.FileId}");
        }

        return new StabilityCheckResult(FileStatus.Stable);
    }

    /// <summary>
    /// Cria snapshot de metadados a partir de FileInfo (produção).
    /// </summary>
    public static MetadataSnapshot CapturarDesdeFileInfo(FileInfo info) =>
        new(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).Ticks, info.FullName);

    /// <summary>
    /// Cria snapshot a partir de FileEntry (já possui size/mtime/file_id).
    /// </summary>
    public static MetadataSnapshot CapturarDesdeEntry(FileEntry entry) =>
        new(entry.Size, entry.MtimeUtc.Ticks, entry.FileId);
}
