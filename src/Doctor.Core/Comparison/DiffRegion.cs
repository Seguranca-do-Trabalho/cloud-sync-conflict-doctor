namespace Doctor.Core;

/// <summary>
/// Tipo de região de diff (decisão do orquestrador, card T23 — SPEC §16; ADR-0011):
/// <see cref="Equal"/> trecho casado; <see cref="Added"/> somente no documento right;
/// <see cref="Removed"/> somente no left; <see cref="Changed"/> par substituto
/// (remoção pareada 1:1 com adição dentro do mesmo bloco).
/// </summary>
public enum RegionKind
{
    Equal,
    Added,
    Removed,
    Changed,
}

/// <summary>
/// Trecho de divergência/igualdade entre dois documentos. Índices 0-based com
/// CONTAGENS de elementos (nunca índice final). Regiões consecutivas particionam
/// ambos os documentos em ordem: soma(LeftCount) == linhas de left e
/// soma(RightCount) == linhas de right.
/// </summary>
public sealed record DiffRegion(
    RegionKind Kind,
    int LeftStart,
    int LeftCount,
    int RightStart,
    int RightCount);
