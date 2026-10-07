from celery import shared_task


@shared_task
def send_welcome_email(customer_id):
    # Runs on a single Celery worker fed by the same Redis instance used for the cache.
    return f"welcome sent to {customer_id}"
