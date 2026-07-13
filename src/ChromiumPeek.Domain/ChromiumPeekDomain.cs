using UiaPeek.Domain;

namespace ChromiumPeek.Domain
{
    /// <summary>
    /// Default <see cref="IChromiumPeekDomain"/>. Receives the individual domain services by
    /// constructor injection and exposes them as properties. Registered as a transient so it
    /// can safely hold the singleton launcher and the transient repository together.
    /// </summary>
    public class ChromiumPeekDomain(
        IChromiumPeekLauncher launcher,
        IChromiumPeekRepository repository) : IChromiumPeekDomain
    {
        #region *** Properties ***
        /// <inheritdoc />
        public IChromiumPeekLauncher Launcher { get; set; } = launcher;

        /// <inheritdoc />
        public IChromiumPeekRepository Repository { get; set; } = repository;
        #endregion
    }
}
