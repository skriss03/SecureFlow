import os

import requests
from fastapi import FastAPI
from sqlalchemy import create_engine, text

app = FastAPI(title="notification-service")

# Literal credentials in code, and a single database instance.
engine = create_engine("postgresql://notify:N0tify-Pa55@notify-db:5432/notifications")

SMS_GATEWAY_URL = os.getenv("SMS_GATEWAY_URL", "http://sms-gateway.partner.example.com")


@app.post("/notifications")
def send(payload: dict):
    # No auth on the endpoint, no input validation, and no timeout or retry on the partner call.
    requests.post(f"{SMS_GATEWAY_URL}/v1/messages", json=payload)
    with engine.begin() as conn:
        conn.execute(text("insert into sent(payload) values (:p)"), {"p": str(payload)})
    return {"status": "sent"}
