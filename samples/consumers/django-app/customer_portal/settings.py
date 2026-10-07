from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent.parent

# Production settings committed with debug on, a literal secret key and every host allowed.
SECRET_KEY = "django-insecure-7c#q!x2v@m9k$w4r8t%y6u^i3o&p1a*s5d"
DEBUG = True
ALLOWED_HOSTS = ["*"]

INSTALLED_APPS = [
    "django.contrib.auth",
    "django.contrib.contenttypes",
    "django.contrib.sessions",
    "accounts",
]

ROOT_URLCONF = "customer_portal.urls"

DATABASES = {
    "default": {
        "ENGINE": "django.db.backends.postgresql",
        "NAME": "customers",
        "USER": "portal",
        "PASSWORD": "Portal#2024",
        "HOST": "customers-db",
        "PORT": "5432",
    }
}

CACHES = {
    "default": {
        "BACKEND": "django_redis.cache.RedisCache",
        "LOCATION": "redis://redis:6379/0",
    }
}

CELERY_BROKER_URL = "redis://redis:6379/1"

CRM_API_URL = "http://crm.internal.example.com/api"

SESSION_COOKIE_SECURE = False
CSRF_COOKIE_SECURE = False
