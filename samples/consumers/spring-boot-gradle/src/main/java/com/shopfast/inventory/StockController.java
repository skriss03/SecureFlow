package com.shopfast.inventory;

import java.util.Map;

import org.springframework.kafka.annotation.KafkaListener;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.reactive.function.client.WebClient;

@RestController
@RequestMapping("/api/stock")
public class StockController {
    private final WebClient warehouse;

    public StockController(WebClient warehouse) {
        this.warehouse = warehouse;
    }

    @PostMapping("/reserve")
    public Map<?, ?> reserve(@RequestBody Map<String, Object> request) {
        return warehouse.post().uri("/reservations").bodyValue(request).retrieve().bodyToMono(Map.class).block();
    }

    @KafkaListener(topics = "order-placed", groupId = "inventory")
    public void onOrderPlaced(String orderId) {
        warehouse.post().uri("/picklists/" + orderId).retrieve().toBodilessEntity().block();
    }
}
