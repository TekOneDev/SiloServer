namespace Silo.Server.Core;

/// <summary>
/// Hook for search by meaning, parked until keyword search is signed off. A provider embeds page chunks at
/// index time and answers nearest-neighbour queries; the searcher would fuse its results with BM25 by
/// reciprocal rank (as Silo Desktop does). Models load from DataDir\models\&lt;name&gt;\silo-model.json
/// (format "silo.embedding/1": model.onnx, tokenizer.json, dims, pooling, prefixes, chunk_words).
/// Not implemented in this build: settings.semantic is ignored.
/// </summary>
public interface ISemanticProvider
{
    string Name { get; }
    int Dims { get; }
    /// <summary>Embed the chunks of one item (called by the indexer after text is final).</summary>
    void Index(long itemId, IReadOnlyList<(int page, string text)> chunks);
    void Delete(long itemId);
    /// <summary>Nearest items for a query, best first, restricted to item ids the caller may see.</summary>
    IReadOnlyList<(long itemId, int page, float similarity)> Search(string query, int k, Func<long, bool> allowed);
}
