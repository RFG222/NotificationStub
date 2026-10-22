
# Этап сборки приложения
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала копируем файл проекта и восстанавливаем зависимости
COPY NotificationStub.csproj ./
RUN dotnet restore NotificationStub.csproj

# Копируем исходный код и публикуем приложение
COPY . ./
RUN dotnet publish NotificationStub.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# Этап запуска: используем более лёгкий runtime-образ
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Приложение будет слушать порт 8080 внутри контейнера
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "NotificationStub.dll"]
