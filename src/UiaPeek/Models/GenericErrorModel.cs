using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace UiaPeek.Models
{
    /// <summary>
    /// Represents a generic error model used to provide standardized error responses.
    /// </summary>
    /// <param name="httpContext">The HTTP context of the current request.</param>
    public class GenericErrorModel(HttpContext httpContext) : ProblemDetails
    {
        #region *** Constructors ***
        /// <summary>
        /// Initializes a new instance of the <see cref="GenericErrorModel"/> class with default values.
        /// </summary>
        public GenericErrorModel()
            : this(httpContext: default)
        {
            // Default constructor calls the parameterized constructor with a null HttpContext.
        }
        #endregion

        #region *** Properties   ***
        /// <summary>
        /// Gets the collection of validation errors associated with the error response.
        /// </summary>
        public Dictionary<string, string[]> Errors { get; set; } = [];

        /// <summary>
        /// Gets the request information associated with the error.
        /// </summary>
        public object Request { get; set; } = httpContext?.Request.Method == "GET"
            ? $"{httpContext.Request.Method} {httpContext.Request.Path} {httpContext.Request.Protocol}"
            : default;

        /// <summary>
        /// Gets the route data associated with the error.
        /// </summary>
        public Dictionary<string, object> RouteData { get; } = [];

        /// <summary>
        /// Gets the unique identifier for tracing the request.
        /// </summary>
        public virtual string TraceId { get; } = httpContext?.TraceIdentifier ?? DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
        #endregion

        #region *** Methods      ***
        /// <summary>
        /// Adds an error message to the error model.
        /// </summary>
        /// <param name="name">The name of the field or property associated with the error.</param>
        /// <param name="value">The error message(s) associated with the field.</param>
        /// <returns>The current instance of <see cref="GenericErrorModel"/>.</returns>
        public GenericErrorModel AddError(string name, params string[] value)
        {
            // Assign the error messages to the specified field in the Errors dictionary.
            Errors[name] = value;

            // Return the current instance to allow method chaining.
            return this;
        }

        /// <summary>
        /// Adds multiple validation errors to the error model.
        /// </summary>
        /// <param name="errors">A dictionary of field names and their corresponding error messages.</param>
        /// <returns>The current instance of <see cref="GenericErrorModel"/>.</returns>
        public GenericErrorModel AddErrors(IDictionary<string, string[]> errors)
        {
            // Iterate through each error in the provided dictionary.
            foreach (var error in errors)
            {
                // Add or update the error messages for each field in the Errors dictionary.
                Errors[error.Key] = error.Value;
            }

            // Return the current instance to allow method chaining.
            return this;
        }

        /// <summary>
        /// Adds validation errors to the <see cref="Errors"/> dictionary.
        /// </summary>
        /// <param name="validationResults">A list of <see cref="ValidationResult"/> instances containing validation errors.</param>
        /// <returns>The current instance of <see cref="GenericErrorModel"/> for method chaining.</returns>
        public GenericErrorModel AddErrors(List<ValidationResult> validationResults)
        {
            foreach (var validationResult in validationResults)
            {
                // Check if there are no member names associated with the validation error
                if (!validationResult.MemberNames.Any())
                {
                    // Add the error message to the list under the key "$"
                    Errors["$"] = [.. Errors["$"], validationResult.ErrorMessage];
                }

                // Iterate over each member name associated with the validation error
                foreach (var memberName in validationResult.MemberNames)
                {
                    // Add the error message to the list under the member name key
                    Errors[memberName] = [validationResult.ErrorMessage];
                }
            }

            // Return the current instance to allow method chaining
            return this;
        }

        /// <summary>
        /// Adds route data to the error model.
        /// </summary>
        /// <param name="routeData">A dictionary containing route data.</param>
        /// <returns>The current instance of <see cref="GenericErrorModel"/>.</returns>
        public GenericErrorModel AddRouteData(IDictionary<string, object> routeData)
        {
            // Iterate through each route data entry in the provided dictionary.
            foreach (var data in routeData)
            {
                // Add or update the route data for each key in the RouteData dictionary.
                RouteData[data.Key] = data.Value;
            }

            // Return the current instance to allow method chaining.
            return this;
        }

        /// <summary>
        /// Sets the request object associated with the error.
        /// </summary>
        /// <param name="request">The request object.</param>
        /// <returns>The current instance of <see cref="GenericErrorModel"/>.</returns>
        public GenericErrorModel SetRequest(object request)
        {
            // Local function to format or sanitize the request object based on its type.
            static object FormatRequest(object request)
            {
                // Implement sanitization logic here if needed.
                return request;
            }

            // Assign the formatted request object to the Request property.
            Request = FormatRequest(request);

            // Return the current instance to allow method chaining.
            return this;
        }
        #endregion
    }
}
