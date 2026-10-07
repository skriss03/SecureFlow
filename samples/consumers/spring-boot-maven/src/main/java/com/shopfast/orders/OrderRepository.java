package com.shopfast.orders;

import java.util.Map;

import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Repository;

@Repository
public class OrderRepository {
    private final JdbcTemplate jdbc;

    public OrderRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public Object save(Map<String, Object> order) {
        return jdbc.update("insert into orders(payload) values (?)", order.toString());
    }
}
