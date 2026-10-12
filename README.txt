REZERV PAYMENT API - PROJECT SETUP AND RUNNING GUIDE
====================================================

Rezerv is an ASP.NET Core (.NET 10) Web API that takes credit-card payments through the 2C2P sandbox.
It uses MySQL (data), Redis (cache and distributed locks) and Seq (log viewer).

There are two ways to run it:

  Option A - Everything in Docker (easiest: only Docker is needed)
  Option B - MySQL, Redis and Seq in Docker, the API from source (for development and debugging)

Full API reference: docs\Rezerv-API-Documentation.docx


----------------------------------------------------------------------
1. PREREQUISITES
----------------------------------------------------------------------

  Tool                      Needed for       Check with
  ------------------------  ---------------  -------------------------
  Docker Desktop            Options A and B  docker --version
                                             docker compose version
  .NET 10 SDK               Option B, tests  dotnet --version   (10.x)
  Visual Studio / VS Code   optional         -
  / Rider
  ngrok (or a VS dev        optional, for    ngrok version
  tunnel)                   2C2P backend
                            notifications

Start Docker Desktop and wait until it says "Engine running" before you continue.

Ports used on your machine:

  5080  API in Docker           (http://localhost:5080)
  5222  API from source         (http://localhost:5222)
  3308  MySQL in Docker         (user rezerv / password rezerv, database rezerv)
  6380  Redis in Docker
  5341  Seq log viewer          (http://localhost:5341)

If one of these ports is already in use, see "Changing ports" in section 6.


----------------------------------------------------------------------
2. GET THE PROJECT
----------------------------------------------------------------------

  1. Copy or clone the project folder, for example to:
       E:\Visual Studio Projects\RezervHomeTest\Rezerv

  2. Open PowerShell in that folder. Run every command below from there:
       cd "E:\Visual Studio Projects\RezervHomeTest\Rezerv"

  Folder layout:

    Rezerv.slnx               Solution file (open it in Visual Studio)
    docker-compose.yml        MySQL, Redis, Seq and the API
    Dockerfile                Builds the API image
    src\Rezerv.Domain         Entities
    src\Rezerv.Application    Services, payment rules, interfaces
    src\Rezerv.Infrastructure EF Core (MySQL), Redis, 2C2P gateways, seed data
    src\Rezerv.API            Controllers, Swagger, Program.cs, appsettings
    test\Rezerv.Test          xUnit tests
    scripts                   concurrent-checkout.ps1 (idempotency/concurrency test)
    docs                      Word documentation


----------------------------------------------------------------------
3. OPTION A - RUN EVERYTHING IN DOCKER
----------------------------------------------------------------------

Step 1. Build and start all containers:

    docker compose up -d --build

  The first run downloads the images and builds the API, so it takes a few minutes.
  Four containers start in the Docker project "rezerv-home": mysql, redis, seq and api.
  The API waits until MySQL and Redis are healthy.

Step 2. Check that everything is running:

    docker compose ps

  All four services should be "running". mysql and redis should also be "healthy".

Step 3. Watch the API start (optional; press Ctrl+C to stop watching):

    docker compose logs -f api

  On the first start, the API creates the database schema and seeds the customers and products.
  When the log shows "Now listening on: http://[::]:8080", the API is ready.

Step 4. Open the API:

    Swagger UI : http://localhost:5080/swagger
    Seq logs   : http://localhost:5341

Step 5. Quick test (PowerShell):

    Invoke-RestMethod http://localhost:5080/api/customers
    Invoke-RestMethod http://localhost:5080/api/products

  You should see 3 customers (Alice, Bob, Chloe) and 4 products.


----------------------------------------------------------------------
4. OPTION B - RUN THE API FROM SOURCE
----------------------------------------------------------------------

Step 1. Start only the infrastructure in Docker:

    docker compose up -d mysql redis seq

Step 2. Point the API at the MySQL running in Docker.
  src\Rezerv.API\appsettings.Development.json points to a MySQL installed on this machine (port 3306).
  To use the MySQL in Docker instead, set the connection string in the same PowerShell window before you run the API:

    $env:ConnectionStrings__DefaultConnection = "Server=localhost;Port=3308;Database=rezerv;User=rezerv;Password=rezerv;"

  Alternatively, change "DefaultConnection" in appsettings.Development.json.
  Redis is already set to localhost:6380 and Seq to http://localhost:5341.

Step 3. Restore, build and run:

    dotnet restore
    dotnet build
    dotnet run --project src\Rezerv.API

  The API listens on http://localhost:5222.

  In Visual Studio, open Rezerv.slnx, set Rezerv.API as the startup project, choose the "http" profile and press F5.
  The $env: setting only applies to the PowerShell window, so with Visual Studio set the connection string in
  appsettings.Development.json instead.

Step 4. Open the API:

    Swagger UI : http://localhost:5222/swagger
    Seq logs   : http://localhost:5341


----------------------------------------------------------------------
5. TRY THE API
----------------------------------------------------------------------

Use Swagger, or the PowerShell commands below. For Option B, use port 5222 instead of 5080.

  Seed data:
    Customers  : 1 Alice Tan, 2 Bob Lim, 3 Chloe Ng
    Products   : 1 Single Session Pass  25.00 SGD (OneTime)
                 2 Event Ticket        120.00 SGD (OneTime)
                 3 Monthly Membership   49.90 SGD (Recurring)
                 4 Annual Membership   499.00 SGD (Recurring)
    Saved cards: none. A card is saved when a customer pays for a Recurring product with a new card
                 and ticks "save card" on the 2C2P page. This needs the notification set up (section 7).

  a) Checkout with a new card (2C2P hosted page):

    $headers = @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }
    $body = '{ "customerId": 1, "productId": 3 }'
    Invoke-RestMethod -Method Post http://localhost:5080/api/checkout -Headers $headers -ContentType "application/json" -Body $body

  Open the returned redirectUrl in a browser and pay with a 2C2P sandbox test card,
  for example Visa 4111 1111 1111 1111, any future expiry date and CVV 123.

  b) Payment result (use the invoiceNo from the checkout answer):

    Invoke-RestMethod http://localhost:5080/api/transactions/<invoiceNo>

  c) Saved cards, and removing one:

    Invoke-RestMethod http://localhost:5080/api/customers/1/cards
    Invoke-RestMethod -Method Delete http://localhost:5080/api/customers/1/cards/<cardId>

  d) Pay with a saved card (no hosted page):

    $headers = @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }
    $body = '{ "customerId": 1, "productId": 2, "customerCardId": <cardId> }'
    Invoke-RestMethod -Method Post http://localhost:5080/api/checkout -Headers $headers -ContentType "application/json" -Body $body

  e) A customer's transactions, then a refund (leave out "amount" to refund the full payment):

    Invoke-RestMethod http://localhost:5080/api/transactions/1

    $headers = @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }
    Invoke-RestMethod -Method Post http://localhost:5080/api/transactions/<invoiceNo>/refunds -Headers $headers -ContentType "application/json" -Body '{ "amount": 10.00 }'
    Invoke-RestMethod http://localhost:5080/api/transactions/<invoiceNo>/refunds

  f) Retry a failed payment. This creates a new payment with a new invoiceNo for the same customer and product.
     A failed payment can be retried once; if the retry fails too, retry the new invoiceNo instead.

    $headers = @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }
    Invoke-RestMethod -Method Post http://localhost:5080/api/transactions/<failed invoiceNo>/retry -Headers $headers

    To pay the retry with a saved card instead of the 2C2P page, add a body:
    Invoke-RestMethod -Method Post http://localhost:5080/api/transactions/<failed invoiceNo>/retry -Headers $headers -ContentType "application/json" -Body '{ "customerCardId": <cardId> }'

  README.md and Swagger list every endpoint and request body.


----------------------------------------------------------------------
6. DOCKER COMMANDS
----------------------------------------------------------------------

  Start everything and rebuild the API image (use after code changes):
    docker compose up -d --build

  Start without rebuilding:
    docker compose up -d

  Show the status of the containers:
    docker compose ps

  Show logs:
    docker compose logs -f api          (API only)
    docker compose logs -f              (all services)

  Rebuild and restart only the API after a code change:
    docker compose up -d --build api

  Stop and remove the containers (the data is kept):
    docker compose down

  Stop and DELETE ALL DATA (database, Redis and Seq logs). The next start creates
  and seeds a new database:
    docker compose down -v

  Open a MySQL shell:
    docker compose exec mysql mysql -urezerv -prezerv rezerv

  Open a Redis shell:
    docker compose exec redis redis-cli

  Changing ports:
    You can change the ports on your machine with environment variables, for example:
      $env:API_HOST_PORT = "5090"
      $env:MYSQL_HOST_PORT = "3309"
      $env:REDIS_HOST_PORT = "6381"
      $env:SEQ_HOST_PORT = "5342"
      docker compose up -d --build
    For Option B, also change the connection strings to the new ports.


----------------------------------------------------------------------
7. 2C2P BACKEND NOTIFICATION (OPTIONAL)
----------------------------------------------------------------------

After a hosted-page payment, 2C2P POSTs the result to BackendReturnUrl. 2C2P must be able to reach this URL
from the internet, so expose your local API with a tunnel:

  Option A (Docker, port 5080):
    ngrok http 5080
    $env:TWOC2P_BACKEND_RETURN_URL = "https://<your-subdomain>.ngrok-free.app/api/payments/2c2p/notification"
    docker compose up -d api

  Option B (from source, port 5222):
    ngrok http 5222
    $env:_2C2P__BackendReturnUrl = "https://<your-subdomain>.ngrok-free.app/api/payments/2c2p/notification"
    dotnet run --project src\Rezerv.API

Without a tunnel, payments still finish: GET /api/transactions/{invoiceNo} asks 2C2P for the result
(Payment Inquiry). No card is saved, though, because the card token only arrives in the notification.


----------------------------------------------------------------------
8. RUN THE TESTS
----------------------------------------------------------------------

  Unit tests (Docker, MySQL and Redis are not needed):

    dotnet test

  Concurrency test for checkout idempotency (the API must be running):

    .\scripts\concurrent-checkout.ps1 -BaseUrl http://localhost:5080     (Option A)
    .\scripts\concurrent-checkout.ps1                                    (Option B, port 5222)
    .\scripts\concurrent-checkout.ps1 -Count 50                          (50 parallel requests)

  If PowerShell blocks the script, allow scripts for this PowerShell window only:

    Set-ExecutionPolicy -Scope Process Bypass


----------------------------------------------------------------------
9. TROUBLESHOOTING
----------------------------------------------------------------------

  Docker says "port is already allocated"
    Another program is using the port. Change the port (section 6) or stop the other program.

  The API log shows "Database not ready (attempt n/10)"
    MySQL is still starting. The API retries automatically.

  Option B: "Unable to connect to any of the specified MySQL hosts"
    The connection string still points to port 3306. Set it to port 3308 (section 4, step 2),
    and check that "docker compose ps" shows mysql as healthy.

  "Unknown column ..." errors after updating the code
    An older version created the database. Recreate it:
      docker compose down -v
      docker compose up -d --build

  A hosted-page payment stays "Pending"
    2C2P cannot reach the notification URL. Set up the tunnel (section 7), or call
    GET /api/transactions/{invoiceNo}, which asks 2C2P for the result.

  Removing a card returns 502
    2C2P did not confirm the removal, so the card was kept. Look up the respCode in Seq (http://localhost:5341).

  Retrying a payment returns 409 "already been retried as INV..."
    Each failed payment can be retried only once, so it can never be charged twice. Open the invoiceNo
    named in the message: if that payment failed too, retry that one.
