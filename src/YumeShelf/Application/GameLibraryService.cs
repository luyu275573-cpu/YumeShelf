using YumeShelf.Domain;
using YumeShelf.Infrastructure;

namespace YumeShelf.Application;

public sealed class GameLibraryService
{
    private readonly JsonGameStore _store;

    public GameLibraryService(JsonGameStore store) => _store = store;
    public IReadOnlyList<Game> Load(bool throwOnError = false) => _store.Load(throwOnError);
    public void Save(IEnumerable<Game> games) => _store.Save(games);
}
