import pika, os
import json

# Access the CLODUAMQP_URL environment variable and parse it (fallback to localhost)
url = os.environ.get('CLOUDAMQP_URL', 'amqps://avfepwdu:SS4fTAg36RK1hPQAUnyC6TH-4Mf3uyJo@fox.rmq.cloudamqp.com/avfepwdu')
params = pika.URLParameters(url)
connection = pika.BlockingConnection(params)
channel = connection.channel() # start a channel
channel.queue_declare(queue='CheckIn', durable = True)
def callback(ch, method, properties, body):
  body_json = json.loads(body.decode("utf-8"))
  timeStamp = body_json.get('TimeStamp')
  checkInBeginTime = 0;
  checkInEndTime = 0;
  isChecked = False
  gateNum = 0
  print(body_json)
  if body_json.get('Ticket')==None:
      body_json.update({'Reason': 'No ticket'})
      print("Passenger", body_json.get('PassengerId'), "don't have a ticket to check in")
  else:
      flightId = body_json.get('Ticket').get('FlightId')
      # здесь будет get запрос для получение рейса
      # http_response = requests.get('')  getById
      http_response = b'{"FlightId":"1","HasVips":true}'  # просто для теста
      response_json = json.loads(http_response.decode("utf-8"))
      checkInBeginTime = response_json.get('checkInBeginTime')
      checkInEndTime = response_json.get('checkInEndTime')
      gateNum = response_json.get('gateNum')
      if (checkInBeginTime <= timeStamp < checkInEndTime):
          isChecked = True
          body_json.update({'GateNum': gateNum})
          #получаем время по ресту
          #body_json.update({'TimeStamp': timeNow})
          channel.basic_publish(exchange='',
                                routing_key='gatePlacement',
                                body=json.dumps(body_json))
          print("Passenger", body_json.get('PassengerId'), "check in")
      if (timeStamp < checkInBeginTime):
          body_json.update({'Reason': 'Early'})
          print("Passenger", body_json.get('PassengerId'), "came early")
      if (timeStamp > checkInEndTime):
          body_json.update({'Reason': 'Late'})
          print("Passenger", body_json.get('PassengerId'), "came late")
  body_json.update({'IsChecked': isChecked})
  channel.basic_publish(exchange='',
                        routing_key='CheckInToPassengerQueue',
                        body=json.dumps(body_json))
  print("Sent", json.dumps(body_json), "\n")

channel.basic_consume('CheckIn',
                      callback,
                      auto_ack=True)

print(' [*] Waiting for messages:')
channel.start_consuming()
connection.close()