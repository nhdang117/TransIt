namespace TransIt.Modes;

public interface ITranslationMode
{
    Task ActivateAsync(CancellationToken ct);
    void Deactivate();
}
