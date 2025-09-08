using G4.Extensions;

using Microsoft.AspNetCore.Mvc.Formatters;

using System.Net.Mime;
using System.Threading.Tasks;

namespace UiaPeek.Domain.Formatters
{
    /// <summary>
    /// An input formatter that handles plain text content types.
    /// </summary>
    public class PlainTextInputFormatter : InputFormatter
    {
        #region *** Constants    ***
        // Constant representing the plain text media type.
        private const string ContentType = MediaTypeNames.Text.Plain;
        #endregion

        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="PlainTextInputFormatter"/> class.
        /// </summary>
        public PlainTextInputFormatter()
        {
            // Add the supported media type (plain text) to the list of supported media types.
            SupportedMediaTypes.Add(ContentType);
        }
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Asynchronously reads an object from the request body.
        /// </summary>
        /// <param name="context">The context for input formatter which contains the HTTP context and model metadata.</param>
        /// <returns>A task that, when completed, returns an <see cref="InputFormatterResult"/> representing the deserialized request body.</returns>
        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
        {
            // Read the request body as a string using the extension method.
            var request = await context.HttpContext.Request.ReadAsync().ConfigureAwait(false);

            // Return the successfully deserialized request body as an InputFormatterResult.
            return await InputFormatterResult.SuccessAsync(request).ConfigureAwait(false);
        }

        /// <summary>
        /// Determines whether the input formatter can read the request body based on the content type.
        /// </summary>
        /// <param name="context">The context for input formatter which contains the HTTP context and model metadata.</param>
        /// <returns><c>true</c> if the content type is supported; otherwise, <c>false</c>.</returns>
        public override bool CanRead(InputFormatterContext context)
        {
            // Get the content type of the request.
            var contentType = context.HttpContext.Request.ContentType;

            // Check if the content type matches the supported plain text content type.
            return contentType?.StartsWith(ContentType) == true;
        }
        #endregion
    }
}
