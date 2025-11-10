# ArbitrageGainer Starter

Starter Code for Arbitrage Gainer Project

## Prerequisites
- Docker Desktop (or Docker Engine)
- `make`


## Start
```bash
    make up
    curl http://localhost:8082/health   # Expected -> {"status":"ok"}
```

## Stop
```bash
    make down
```

## Ports

* Container listens on 0.0.0.0:8080.

* Host maps to 8082 by default. To use another port:

```bash
    APP_PORT=8090 make up
    curl http://localhost:8090/health
```


## Makefile Commands

| Command             | Description                                      |
|---------------------|--------------------------------------------------|
| `make up`           | Build and start all containers                   |
| `make down`         | Stop and remove all containers, volumes, networks|
| `make rebuild`      | Force rebuild the app image                      |
| `make who-owns-port`| Check which process or container uses your port  |

## Cross-Traded Pairs Workflow (Milestone II)

| Endpoint | Description |
|----------|-------------|
| `POST /pairs/cross-traded` | Triggers identification of cross-traded pairs across Bitfinex, Bitstamp, and Kraken, persists the intersection, and logs start/end timestamps (see `Logging.Logger`). |
| `GET /pairs/cross-traded` | Returns the latest cached snapshot (UTC timestamp + `currency1-currency2` pairs). |

Implementation follows the layered workflow rules:

- **Core (Domain):** Currency/Exchange types, domain errors, and snapshot records live under `src/Core/Model`.
- **Services:** `CrossTradedPairsWorkflow` composes the workflow with railroad-style result propagation and immutable data.
- **Infrastructure:** HTTP adapters and persistence live under `src/Infrastructure/Adapters`; all external APIs are isolated here.
- **Application:** `CompositionRoot` wires dependencies (fetchers + repository) and exposes DI-friendly APIs to the web layer.
- **Web:** Suave controllers log performance metrics at entry and delegate to workflows via dependency injection.

### Testing & Validation

An Expecto-based suite (`src/Tests/CrossTradedPairsWorkflow.Tests`) verifies set intersection, error propagation, and repository integration. Run it (or the entire solution) with:

```bash
dotnet test src/Tests/CrossTradedPairsWorkflow.Tests/CrossTradedPairsWorkflow.Tests.fsproj
```

## Using MongoDB Atlas (Hosted)

By default the app points to the local `mongo` service via `MONGO_URI`.

To use a hosted MongoDB Atlas cluster, simply update the MONGO_URI in your docker-compose.yml

```docker-compose
services:
  app:
    environment:
      ASPNETCORE_URLS: http://0.0.0.0:8080
      MONGO_URI: <YOUR_MONGO_URI>
```
