# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY dotnet/Directory.Packages.props dotnet/
COPY dotnet/Microsoft.McpGateway.sln dotnet/
COPY dotnet/Microsoft.McpGateway.Management/src/Microsoft.McpGateway.Management.csproj dotnet/Microsoft.McpGateway.Management/src/
COPY dotnet/Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj dotnet/Microsoft.McpGateway.Service/src/

# Restore dependencies
RUN dotnet restore dotnet/Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj

# Copy the rest of the source code
COPY dotnet/Microsoft.McpGateway.Management/src dotnet/Microsoft.McpGateway.Management/src/
COPY dotnet/Microsoft.McpGateway.Service/src dotnet/Microsoft.McpGateway.Service/src/

# Build and publish the application
WORKDIR /src/dotnet/Microsoft.McpGateway.Service/src
RUN dotnet publish Microsoft.McpGateway.Service.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r -g 1000 appuser && useradd -r -u 1000 -g appuser appuser

# Copy published application from build stage with proper ownership
COPY --from=build --chown=appuser:appuser /app/publish .

USER appuser

# Expose port
EXPOSE 8080

# Set entrypoint
ENTRYPOINT ["dotnet", "Microsoft.McpGateway.Service.dll"]
