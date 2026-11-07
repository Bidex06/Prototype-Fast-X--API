# Use the official .NET 8 SDK image to build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files and restore dependencies
COPY ["TradingBotEngine.API/TradingBotEngine.API.csproj", "TradingBotEngine.API/"]
COPY ["TradingBotEngine.Core/TradingBotEngine.Core.csproj", "TradingBotEngine.Core/"]
COPY ["TradingBotEngine.Data/TradingBotEngine.Data.csproj", "TradingBotEngine.Data/"]
COPY ["TradingBotEngine.Services/TradingBotEngine.Services.csproj", "TradingBotEngine.Services/"]
RUN dotnet restore "TradingBotEngine.API/TradingBotEngine.API.csproj"

# Copy the rest of the files and build
COPY . .
WORKDIR "/src/TradingBotEngine.API"
RUN dotnet publish "TradingBotEngine.API.csproj" -c Release -o /app/publish

# Use the runtime image for the final container
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TradingBotEngine.API.dll"]   