from django.urls import path

from accounts import views

urlpatterns = [
    path("api/customers/<int:customer_id>/", views.customer_profile),
]
