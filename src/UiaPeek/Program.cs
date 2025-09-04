using CommandBridge;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using UiaPeek.Extensions;

var command = CommandBase.FindCommand(args);
if(command != null )
{
    command?.Invoke(args);
    return;
}

// Write the ASCII logo for the Hub Controller with the specified version.
ControllerUtilities.WriteHubAsciiLogo(version: "0000.00.00.0000");

// Create a new instance of the WebApplicationBuilder with the provided command-line arguments.
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
