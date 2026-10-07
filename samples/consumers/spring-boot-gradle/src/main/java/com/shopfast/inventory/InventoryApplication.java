package com.shopfast.inventory;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.context.annotation.Bean;
import org.springframework.web.reactive.function.client.WebClient;

@SpringBootApplication
public class InventoryApplication {
    public static void main(String[] args) {
        SpringApplication.run(InventoryApplication.class, args);
    }

    // No response timeout and no retry/circuit breaker on the warehouse call.
    @Bean
    public WebClient warehouseClient(WarehouseProperties props) {
        return WebClient.builder().baseUrl(props.getBaseUrl()).build();
    }
}
