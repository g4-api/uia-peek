using UiaPeek.Domain;

namespace ChromiumPeek.Domain
{
    /// <summary>
    /// Aggregates the ChromiumPeek domain services behind a single injectable facade.
    /// Consumers (controllers, hubs) inject this domain and reach the individual services
    /// through its properties, so a new dependency is added once here rather than in every
    /// consumer's constructor.
    /// </summary>
    public interface IChromiumPeekDomain
    {
        #region *** Properties ***
        /// <summary>
        /// Gets or sets the launcher used to start and stop peek browsers.
        /// </summary>
        IChromiumPeekLauncher Launcher { get; set; }

        /// <summary>
        /// Gets or sets the repository used to peek UI Automation elements.
        /// </summary>
        IChromiumPeekRepository Repository { get; set; }
        #endregion
    }
}
