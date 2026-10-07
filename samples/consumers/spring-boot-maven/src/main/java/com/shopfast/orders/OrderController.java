package com.shopfast.orders;

import java.util.Map;

import org.springframework.beans.factory.annotation.Value;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.client.RestTemplate;

@RestController
@RequestMapping("/api/orders")
public class OrderController {
    private final RestTemplate http;
    private final KafkaTemplate<String, String> kafka;
    private final OrderRepository orders;

    @Value("${inventory.base-url}")
    private String inventoryUrl;

    @Value("${payment.base-url}")
    private String paymentUrl;

    public OrderController(RestTemplate http, KafkaTemplate<String, String> kafka, OrderRepository orders) {
        this.http = http;
        this.kafka = kafka;
        this.orders = orders;
    }

    // No authentication, no input validation; inventory and payment are called synchronously in sequence.
    @PostMapping
    public Map<String, Object> place(@RequestBody Map<String, Object> order) {
        http.postForObject(inventoryUrl + "/api/stock/reserve", order, Map.class);
        http.postForObject(paymentUrl + "/v1/charges", order, Map.class);
        Object id = orders.save(order);
        kafka.send("order-placed", String.valueOf(id));
        return Map.of("id", id, "status", "PLACED");
    }
}
