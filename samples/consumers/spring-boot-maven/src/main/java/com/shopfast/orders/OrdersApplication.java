package com.shopfast.orders;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.context.annotation.Bean;
import org.springframework.web.client.RestTemplate;

@SpringBootApplication
public class OrdersApplication {
    public static void main(String[] args) {
        SpringApplication.run(OrdersApplication.class, args);
    }

    // No connect/read timeout and no circuit breaker: a slow inventory or payment service hangs order threads.
    @Bean
    public RestTemplate restTemplate() {
        return new RestTemplate();
    }
}
