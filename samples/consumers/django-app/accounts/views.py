import requests
from django.conf import settings
from django.core.cache import cache
from django.http import JsonResponse

from .tasks import send_welcome_email


def customer_profile(request, customer_id):
    # No login_required, and a synchronous CRM call with no timeout on every cache miss.
    profile = cache.get(f"customer:{customer_id}")
    if profile is None:
        profile = requests.get(f"{settings.CRM_API_URL}/customers/{customer_id}").json()
        cache.set(f"customer:{customer_id}", profile, 300)
    send_welcome_email.delay(customer_id)
    return JsonResponse(profile)
