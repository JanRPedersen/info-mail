FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY InfoMail.csproj .
RUN dotnet restore InfoMail.csproj
COPY Program.cs Worker.cs ./
COPY Models/ Models/
COPY Services/ Services/
RUN dotnet publish InfoMail.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
COPY appsettings.json .
COPY Data/routing-catalog.json Data/routing-catalog.json
USER $APP_UID
ENTRYPOINT ["dotnet", "InfoMail.dll", "--azure-read-only-pilot"]
