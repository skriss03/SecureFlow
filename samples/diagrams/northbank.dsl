workspace "NorthBank Payments" "Retail payments: card payments, fraud checks and settlement." {

  model {
    group "Internet" {
      customer = person "Customer" "Mobile & Web banking"
      cardnet = softwareSystem "Card Network" "Visa / Mastercard" "External"
    }
    northbank = softwareSystem "NorthBank Payments" {
      group "DMZ" {
        gateway = container "API Gateway" "x2" "Azure APIM"
      }
      group "Private Network" {
        api = container "Payments API" "x2" ".NET"
        fraud = container "Fraud Service" "x1" "Python"
        queue = container "Settlement Queue" "" "Service Bus" "Queue"
        worker = container "Settlement Worker" "x1" ""
        db = container "Payments DB" "x1" "SQL Server" "Database"
        vault = container "Key Vault" "" ""
        monitor = container "Monitoring" "" "App Insights"
      }
    }

    customer -> gateway "OAuth" "HTTPS"
    gateway -> api "JWT" "HTTPS"
    api -> fraud "no auth" "HTTP"
    api -> db "SQL" "SQL"
    api -> queue "publish" "AMQPS" "Async"
    queue -> worker "subscribe" "AMQPS" "Async"
    worker -> cardnet "mTLS" "HTTPS"
    worker -> db "SQL" "SQL"
    api -> vault "managed identity" "HTTPS"
    api -> monitor "telemetry" "async" "Async"
  }

  views {
    container northbank {
      include *
      autolayout lr
    }
    styles {
      element "Database" {
        shape cylinder
      }
      element "Queue" {
        shape pipe
      }
    }
  }
}
