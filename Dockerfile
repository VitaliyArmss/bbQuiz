FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .

RUN dotnet publish -c Release -o /app/publish

# ןנמגונךא קעמ פאיכ נואכüםמ וסעü
RUN ls -la /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

COPY --from=build /app/publish/ ./

RUN ls -la /app

ENTRYPOINT ["dotnet", "bbQuiz.dll"]