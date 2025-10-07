# ---------- STAGE 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia y restaura
COPY . .
RUN dotnet restore

# Publica (Release) a /app/publish
RUN dotnet publish SuperMegaSistema_Epi.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- STAGE 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copia el publish (DLL + contenido)
COPY --from=build /app/publish . 

# BLINDAJE: copia wwwroot explícitamente por si el publish no la incluyó
# (si ya está, esta copia es redundante e inofensiva)
COPY --from=build /src/wwwroot ./wwwroot

# Render define PORT; ASP.NET debe escuchar en todas las interfaces
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet","SuperMegaSistema_Epi.dll"]
