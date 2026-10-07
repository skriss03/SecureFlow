import React from 'react';
import ReactDOM from 'react-dom';
import { listOrders } from './api/client';

function App() {
  const [orders, setOrders] = React.useState<unknown[]>([]);
  React.useEffect(() => { listOrders().then(r => setOrders(r.data)); }, []);
  return <pre>{JSON.stringify(orders, null, 2)}</pre>;
}

ReactDOM.render(<App />, document.getElementById('root'));
