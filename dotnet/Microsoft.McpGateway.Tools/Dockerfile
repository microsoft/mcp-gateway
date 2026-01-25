# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY dotnet/Directory.Packages.props dotnet/
COPY dotnet/Microsoft.McpGateway.sln dotnet/
COPY dotnet/Microsoft.McpGateway.Management/src/Microsoft.McpGateway.Management.csproj dotnet/Microsoft.McpGateway.Management/src/
COPY dotnet/Microsoft.McpGateway.Tools/src/Microsoft.McpGateway.Tools.csproj dotnet/Microsoft.McpGateway.Tools/src/

# Restore dependencies
RUN dotnet restore dotnet/Microsoft.McpGateway.Tools/src/Microsoft.McpGateway.Tools.csproj

# Copy the rest of the source code
COPY dotnet/Microsoft.McpGateway.Management/src dotnet/Microsoft.McpGateway.Management/src/
COPY dotnet/Microsoft.McpGateway.Tools/src dotnet/Microsoft.McpGateway.Tools/src/

# Build and publish the application
WORKDIR /src/dotnet/Microsoft.McpGateway.Tools/src
RUN dotnet publish Microsoft.McpGateway.Tools.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy published application from build stage
COPY --from=build /app/publish .

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser && \
    chown -R appuser:appuser /app
USER appuser

# Expose port
EXPOSE 8080

# Set entrypoint
ENTRYPOINT ["dotnet", "Microsoft.McpGateway.Tools.dll"]
